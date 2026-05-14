using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Domain.Entities;
using AlphaStack.Domain.Enums;
using AlphaStack.Infrastructure.ExternalServices.Fyers;

namespace AlphaStack.Infrastructure.BackgroundServices;

/// <summary>
/// Automatically syncs option chain instruments from Fyers every day at 8:00 IST.
/// Supports multiple underlyings (NIFTY, BANKNIFTY, …) driven by the
/// InstrumentSync:Underlyings config section — adding a new underlying requires
/// only a config change, no code change.
///
/// Per underlying, syncs:
///   - Next N weekly expiries (configurable via WeeksAhead)
///   - Strikes within StrikeInterval × 60 pts of current spot
///   - Both PE and CE for each strike
/// </summary>
public class InstrumentSyncService : BackgroundService, IInstrumentSyncState
{
    private readonly IServiceScopeFactory              _scopeFactory;
    private readonly IConfiguration                    _configuration;
    private readonly ILogger<InstrumentSyncService>    _logger;
    private readonly IHttpClientFactory                _httpClientFactory;
    private readonly FyersTokenService _tokenService;
    
    public bool LastSyncWasSynthetic { get; private set; }

    private static readonly TimeZoneInfo Ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
    private static readonly TimeOnly     SyncTime = new(8, 0);

    // ── Fyers spot-quote symbols keyed by underlying name ─────────────────────
    private static readonly Dictionary<string, string> FyersSpotSymbols =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["NIFTY"]     = "NSE%3ANIFTY50-INDEX",
            ["BANKNIFTY"] = "NSE%3ANIFTYBANK-INDEX",
        };

    // ── Fyers option-chain symbols keyed by underlying name ───────────────────
    private static readonly Dictionary<string, string> FyersChainSymbols =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["NIFTY"]     = "NSE%3ANIFTY50-INDEX",
            ["BANKNIFTY"] = "NSE%3ANIFTYBANK-INDEX",
        };

    public InstrumentSyncService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        FyersTokenService tokenService,
        ILogger<InstrumentSyncService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _tokenService = tokenService;
    }

    // ── Background loop ───────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("[InstrumentSync] Service started.");

        await SyncAllAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            var nextRun = GetNextSyncTime();
            var delay   = nextRun - DateTime.UtcNow;

            _logger.LogInformation(
                "[InstrumentSync] Next sync at {NextRun:HH:mm} IST (in {Delay:hh\\:mm})",
                TimeZoneInfo.ConvertTimeFromUtc(nextRun, Ist), delay);

            using var wakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delayTask = Task.Delay(delay, wakeCts.Token);
            var refreshTask = _tokenService.WaitForTokenRefreshAsync(wakeCts.Token);
            var completed = await Task.WhenAny(delayTask, refreshTask);
            await wakeCts.CancelAsync();

            if (ct.IsCancellationRequested) break;

            if (completed == refreshTask)
                _logger.LogInformation("[InstrumentSync] Fyers token refreshed — running sync now.");

            await SyncAllAsync(ct);
        }
    }

    private DateTime GetNextSyncTime()
    {
        var nowIst       = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Ist);
        var todaySync    = nowIst.Date.Add(SyncTime.ToTimeSpan());
        var nextSyncIst  = nowIst > todaySync ? todaySync.AddDays(1) : todaySync;
        return TimeZoneInfo.ConvertTimeToUtc(nextSyncIst, Ist);
    }

    // ── Top-level sync: iterate every configured underlying ───────────────────

    private async Task SyncAllAsync(CancellationToken ct)
    {
        LastSyncWasSynthetic = false;

        var underlyings = _configuration
            .GetSection("InstrumentSync:Underlyings")
            .Get<List<UnderlyingConfig>>()
            ?? [];

        if (underlyings.Count == 0)
        {
            _logger.LogWarning(
                "[InstrumentSync] No underlyings configured under InstrumentSync:Underlyings — nothing to sync.");
            return;
        }

        _logger.LogInformation(
            "[InstrumentSync] Starting sync for {Count} underlying(s): {Names}",
            underlyings.Count,
            string.Join(", ", underlyings.Select(u => u.Symbol)));

        var allInstruments = new List<Instrument>();

        foreach (var underlying in underlyings)
        {
            try
            {
                var instruments = await SyncUnderlyingAsync(underlying, ct);
                allInstruments.AddRange(instruments);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InstrumentSync] Sync failed for {Symbol}", underlying.Symbol);
            }
        }

        if (allInstruments.Count == 0)
        {
            _logger.LogWarning("[InstrumentSync] No instruments produced across all underlyings — skipping upsert.");
            return;
        }

        // Single DB round-trip for all underlyings combined
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IInstrumentRepository>();
        var uow  = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await repo.BulkUpsertAsync(allInstruments, ct);
        await uow.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[InstrumentSync] ✅ Sync complete — {Total} instruments upserted across all underlyings",
            allInstruments.Count);
    }

    // ── Per-underlying sync ───────────────────────────────────────────────────

    private async Task<List<Instrument>> SyncUnderlyingAsync(
        UnderlyingConfig underlying, CancellationToken ct)
    {
        _logger.LogInformation("[InstrumentSync] [{Symbol}] Starting sync", underlying.Symbol);

        // 1. Current spot price for strike range calculation
        var spot = await FetchSpotAsync(underlying.Symbol, ct);
        if (spot <= 0)
        {
            _logger.LogWarning(
                "[InstrumentSync] [{Symbol}] Could not fetch spot — using default {Default}",
                underlying.Symbol, underlying.DefaultSpot);
            spot = underlying.DefaultSpot;
        }

        _logger.LogInformation("[InstrumentSync] [{Symbol}] Spot: {Spot}", underlying.Symbol, spot);

        // 2. Strike range: StrikeInterval × 60 each side
        var strikeRange = underlying.StrikeInterval * 60;
        var minStrike   = RoundToInterval(spot - strikeRange, underlying.StrikeInterval);
        var maxStrike   = RoundToInterval(spot + strikeRange, underlying.StrikeInterval);

        // 3. Expiry dates
        var expiryDay = Enum.Parse<DayOfWeek>(underlying.ExpiryDay, ignoreCase: true);
        var expiries  = GetNextExpiries(expiryDay, underlying.WeeksAhead).ToArray();

        _logger.LogInformation(
            "[InstrumentSync] [{Symbol}] Expiries: {Expiries} | Strikes: {Min}–{Max}",
            underlying.Symbol,
            string.Join(", ", expiries),
            minStrike, maxStrike);

        // 4. Fetch option chain from Fyers
        var chain = await FetchFyersOptionChainAsync(underlying.Symbol, ct);

        List<Instrument> instruments;
        if (chain.Count > 0)
        {
            instruments = BuildFromFyersChain(chain, underlying, expiries, minStrike, maxStrike);
            _logger.LogInformation(
                "[InstrumentSync] [{Symbol}] Built {Count} instruments from Fyers chain",
                underlying.Symbol, instruments.Count);
        }
        else
        {
            _logger.LogWarning(
                "[InstrumentSync] [{Symbol}] Fyers chain unavailable — skipping sync. " +
                "Last real instruments will be used. Refresh Fyers token to fix.",
                underlying.Symbol);
            LastSyncWasSynthetic = true;
            return [];  // return empty — don't write anything to DB
        }

        // Log per-expiry counts
        foreach (var exp in expiries)
        {
            var count = instruments.Count(i => i.ExpiryDate == exp);
            _logger.LogInformation(
                "[InstrumentSync] [{Symbol}] {Expiry}: {Count} instruments",
                underlying.Symbol, exp, count);
        }

        return instruments;
    }

    // ── Fetch spot price ──────────────────────────────────────────────────────

    private async Task<decimal> FetchSpotAsync(string underlyingSymbol, CancellationToken ct)
    {
        try
        {
            if (!FyersSpotSymbols.TryGetValue(underlyingSymbol, out var fyersSymbol))
            {
                _logger.LogWarning(
                    "[InstrumentSync] [{Symbol}] No Fyers spot symbol mapping — skipping spot fetch",
                    underlyingSymbol);
                return 0;
            }

            var client   = _httpClientFactory.CreateClient("Fyers");
            client.DefaultRequestHeaders.Remove("Authorization");
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "Authorization",
                $"{_configuration["Fyers:ClientId"]}:{_tokenService.AccessToken}");
            var response = await client.GetAsync(
                $"https://api-t1.fyers.in/data/quotes?symbols={fyersSymbol}", ct);

            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) return 0;

            var json = JsonDocument.Parse(body);
            var d    = json.RootElement.GetProperty("d").EnumerateArray().FirstOrDefault();
            if (d.ValueKind == JsonValueKind.Undefined) return 0;

            return d.GetProperty("v").GetProperty("lp").GetDecimal();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[InstrumentSync] [{Symbol}] Failed to fetch spot", underlyingSymbol);
            return 0;
        }
    }

    // ── Fetch option chain from Fyers ─────────────────────────────────────────

    private async Task<List<FyersOptionRecord>> FetchFyersOptionChainAsync(
        string underlyingSymbol, CancellationToken ct)
    {
        try
        {
            if (!FyersChainSymbols.TryGetValue(underlyingSymbol, out var chainSymbol))
            {
                _logger.LogWarning(
                    "[InstrumentSync] [{Symbol}] No Fyers chain symbol mapping — skipping chain fetch",
                    underlyingSymbol);
                return [];
            }

            var client   = _httpClientFactory.CreateClient("Fyers");
            client.DefaultRequestHeaders.Remove("Authorization");
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "Authorization",
                $"{_configuration["Fyers:ClientId"]}:{_tokenService.AccessToken}");
            var url      = $"https://api-t1.fyers.in/data/options-chain-v3?symbol={chainSymbol}&strikecount=60&timestamp=";
            var response = await client.GetAsync(url, ct);
            var body     = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "[InstrumentSync] [{Symbol}] Fyers option chain failed: {Status}",
                    underlyingSymbol, response.StatusCode);
                return [];
            }

            var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("data", out var data)) return [];
            if (!data.TryGetProperty("optionsChain", out var chain)) return [];

            var records = new List<FyersOptionRecord>();
            foreach (var opt in chain.EnumerateArray())
            {
                if (!opt.TryGetProperty("strike_price", out var sp)) continue;
                if (!opt.TryGetProperty("option_type",  out var ot)) continue;
                if (!opt.TryGetProperty("symbol",       out var sym)) continue;
                if (!opt.TryGetProperty("fyToken",      out var tok)) continue;

                var strike = sp.GetDecimal();
                if (strike <= 0) continue;

                records.Add(new FyersOptionRecord(
                    Symbol:     sym.GetString() ?? "",
                    FyToken:    tok.GetString() ?? "",
                    Strike:     strike,
                    OptionType: ot.GetString() ?? "",
                    Ltp:        opt.TryGetProperty("ltp", out var ltp) ? ltp.GetDecimal() : 0m));
            }

            _logger.LogInformation(
                "[InstrumentSync] [{Symbol}] Fetched {Count} option records from Fyers",
                underlyingSymbol, records.Count);

            return records;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[InstrumentSync] [{Symbol}] Failed to fetch Fyers option chain", underlyingSymbol);
            return [];
        }
    }

    // ── Build instruments from Fyers chain ────────────────────────────────────

    private List<Instrument> BuildFromFyersChain(
        List<FyersOptionRecord> chain,
        UnderlyingConfig        underlying,
        DateOnly[]              expiries,
        decimal                 minStrike,
        decimal                 maxStrike)
    {
        var instruments = new List<Instrument>();
        var tokenBase   = ComputeTokenBase(underlying.Symbol);
        var tokenIndex  = 0;

        foreach (var record in chain)
        {
            if (record.Strike < minStrike || record.Strike > maxStrike) continue;

            var optionType = record.OptionType.Equals("PE", StringComparison.OrdinalIgnoreCase)
                ? OptionType.Put : OptionType.Call;

            var expiry = ParseExpiryFromFyersSymbol(record.Symbol, underlying.Symbol, expiries);
            if (expiry is null) continue;

            instruments.Add(Instrument.Create(
                instrumentToken: tokenBase + tokenIndex++,
                tradingSymbol:   record.Symbol.Replace("NSE:", "", StringComparison.OrdinalIgnoreCase),
                name:            underlying.Symbol,
                exchange:        underlying.Exchange,
                instrumentType:  InstrumentType.FuturesAndOptions,
                lotSize:         underlying.LotSize,
                tickSize:        0.05m,
                optionType:      optionType,
                strikePrice:     record.Strike,
                expiryDate:      expiry));
        }

        return instruments;
    }

    // ── Build synthetic instruments (fallback) ────────────────────────────────

    private List<Instrument> BuildSyntheticInstruments(
        UnderlyingConfig underlying,
        DateOnly[]       expiries,
        decimal          minStrike,
        decimal          maxStrike)
    {
        var instruments = new List<Instrument>();
        var tokenBase   = ComputeTokenBase(underlying.Symbol);
        var tokenIndex  = 0;

        foreach (var expiry in expiries)
        {
            var expiryStr = expiry.ToString("ddMMMyy").ToUpperInvariant(); // e.g. 07MAY26

            for (var strike = (int)minStrike; strike <= (int)maxStrike; strike += underlying.StrikeInterval)
            {
                foreach (var (optType, suffix) in new[] { (OptionType.Put, "PE"), (OptionType.Call, "CE") })
                {
                    instruments.Add(Instrument.Create(
                        instrumentToken: tokenBase + tokenIndex++,
                        tradingSymbol:   $"{underlying.Symbol}{expiryStr}{strike}{suffix}",
                        name:            underlying.Symbol,
                        exchange:        underlying.Exchange,
                        instrumentType:  InstrumentType.FuturesAndOptions,
                        lotSize:         underlying.LotSize,
                        tickSize:        0.05m,
                        optionType:      optType,
                        strikePrice:     strike,
                        expiryDate:      expiry));
                }
            }
        }

        _logger.LogInformation(
            "[InstrumentSync] [{Symbol}] Generated {Count} synthetic instruments for expiries: {Expiries}",
            underlying.Symbol, instruments.Count, string.Join(", ", expiries));

        return instruments;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the next <paramref name="count"/> weekly expiry dates for a given
    /// day-of-week, always starting from the first occurrence strictly after today.
    /// </summary>
    private static IEnumerable<DateOnly> GetNextExpiries(DayOfWeek expiryDay, int count)
    {
        var today     = DateOnly.FromDateTime(DateTime.Today);
        var daysAhead = ((int)expiryDay - (int)today.DayOfWeek + 7) % 7;
        if (daysAhead == 0) daysAhead = 7;
        var first = today.AddDays(daysAhead);
        for (var i = 0; i < count; i++)
            yield return first.AddDays(7 * i);
    }

    private static decimal RoundToInterval(decimal value, int interval)
        => Math.Round(value / interval) * interval;

    /// <summary>
    /// Namespace token bases per underlying so synthetic tokens never collide.
    /// NIFTY → 10_000_000, BANKNIFTY → 20_000_000, others get a hash-derived bucket.
    /// </summary>
    private static int ComputeTokenBase(string underlyingSymbol)
    {
        return underlyingSymbol.ToUpperInvariant() switch
        {
            "NIFTY"     => 10_000_000,
            "BANKNIFTY" => 20_000_000,
            _           => (Math.Abs(underlyingSymbol.GetHashCode()) % 89 + 1) * 1_000_000
        };
    }

    /// <summary>
    /// Attempts to match a Fyers option symbol against the known expiry dates.
    ///
    /// Fyers short-date format for weeklies (both NIFTY and BANKNIFTY):
    ///   {UNDERLYING}{YY}{M}{DD}  e.g. NIFTY26512  or  BANKNIFTY26514
    /// where M is the single-digit month (no leading zero) and DD is zero-padded day.
    ///
    /// Falls back to the nearest expiry if no pattern matches (handles edge cases
    /// where Fyers uses a different encoding for monthly expiries).
    /// </summary>
    private static DateOnly? ParseExpiryFromFyersSymbol(
        string     symbol,
        string     underlyingName,
        DateOnly[] knownExpiries)
    {
        foreach (var expiry in knownExpiries)
        {
            var yy      = expiry.Year.ToString()[2..];    // "26"
            var m       = expiry.Month.ToString();         // "5"  (no leading zero)
            var dd      = expiry.Day.ToString("D2");       // "12"
            var pattern = $"{underlyingName.ToUpperInvariant()}{yy}{m}{dd}"; // "NIFTY26512"

            if (symbol.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return expiry;
        }

        // Fallback: assign to nearest expiry rather than silently dropping the record
        return knownExpiries.Length > 0 ? knownExpiries[0] : null;
    }

    // ── Private records ───────────────────────────────────────────────────────

    private record FyersOptionRecord(
        string  Symbol,
        string  FyToken,
        decimal Strike,
        string  OptionType,
        decimal Ltp);
}

// ── Configuration model ───────────────────────────────────────────────────────

/// <summary>
/// Mirrors one entry under <c>InstrumentSync:Underlyings</c> in appsettings.json.
/// </summary>
public sealed class UnderlyingConfig
{
    /// <summary>e.g. "NIFTY" or "BANKNIFTY"</summary>
    public string Symbol        { get; init; } = "";

    /// <summary>Human-readable spot symbol, e.g. "NIFTY 50" or "NIFTY BANK".</summary>
    public string SpotSymbol    { get; init; } = "";

    /// <summary>Options exchange, e.g. "NFO".</summary>
    public string Exchange      { get; init; } = "NFO";

    /// <summary>Strike rounding interval: 50 for NIFTY, 100 for BANKNIFTY.</summary>
    public int StrikeInterval   { get; init; } = 50;

    /// <summary>Lot size for this underlying.</summary>
    public int LotSize          { get; init; } = 25;

    /// <summary>Day-of-week string for weekly expiry, e.g. "Tuesday" or "Wednesday".</summary>
    public string ExpiryDay     { get; init; } = "Tuesday";

    /// <summary>How many successive weekly expiries to sync (default 2).</summary>
    public int WeeksAhead       { get; init; } = 2;

    /// <summary>
    /// Fallback spot used when the live feed is unavailable.
    /// Set conservatively high so the strike range is always wide enough.
    /// </summary>
    public decimal DefaultSpot  { get; init; } = 24_000m;
}
