using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Domain.Entities;
using AlphaStack.Domain.Enums;
using AlphaStack.Infrastructure.ExternalServices.Fyers;
using AlphaStack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlphaStack.Infrastructure.BackgroundServices;

/// <summary>
/// Automatically syncs option chain instruments from Fyers every day at 8:00 IST.
/// Supports multiple underlyings (NIFTY, FINNIFTY, …) driven by the
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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<InstrumentSyncService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FyersTokenService _tokenService;

    public bool LastSyncWasSynthetic { get; private set; }
    public bool IsReady { get; private set; }


    private static readonly TimeZoneInfo Ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
    private static readonly TimeOnly SyncTime = new(9, 0);

    // ── Fyers spot-quote symbols keyed by underlying name ─────────────────────
    private static readonly Dictionary<string, string> FyersSpotSymbols =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["NIFTY"] = "NSE%3ANIFTY50-INDEX",
            ["FINNIFTY"] = "NSE%3AFINNIFTY-INDEX",
        };

    // ── Fyers option-chain symbols keyed by underlying name ───────────────────
    private static readonly Dictionary<string, string> FyersChainSymbols =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["NIFTY"] = "NSE%3ANIFTY50-INDEX",
            ["FINNIFTY"] = "NSE%3AFINNIFTY-INDEX",
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

        if (!_tokenService.IsTokenFreshToday())
        {
            _logger.LogWarning("[InstrumentSync] Token not ready at startup — skipping startup sync. Will run after token refresh.");
        }
        else
        {
            await SyncAllAsync(ct);
        }

        while (!ct.IsCancellationRequested)
        {
            var nextRun = GetNextSyncTime();
            var delay = nextRun - DateTime.UtcNow;

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
        var nowIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Ist);
        var todaySync = nowIst.Date.Add(SyncTime.ToTimeSpan());
        var nextSyncIst = nowIst > todaySync ? todaySync.AddDays(1) : todaySync;
        return TimeZoneInfo.ConvertTimeToUtc(nextSyncIst, Ist);
    }

    // ── Top-level sync: iterate every configured underlying ───────────────────

    public async Task SyncAllAsync(CancellationToken ct)
    {

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

        // Purge expired instruments before sync
        using var purgeScope = _scopeFactory.CreateScope();
        var purgeDb = purgeScope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5).AddMinutes(30)); // IST
        var purged = await purgeDb.Instruments
            .Where(i => i.ExpiryDate < today)
            .ExecuteDeleteAsync(ct);

        if (purged > 0)
            _logger.LogInformation("[InstrumentSync] Purged {Count} expired instruments (expiry < {Today})", purged, today);

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
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var existingSymbols = await repo.GetAllSymbolsAsync(ct);
        var newOnly = allInstruments
            .Where(i => !existingSymbols.Contains(i.TradingSymbol))
            .ToList();

        if (newOnly.Count > 0)
        {
            await repo.BulkUpsertAsync(newOnly, ct);
            await uow.SaveChangesAsync(ct);
        }

        LastSyncWasSynthetic = false;
        IsReady = true;
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
        var minStrike = RoundToInterval(spot - strikeRange, underlying.StrikeInterval);
        var maxStrike = RoundToInterval(spot + strikeRange, underlying.StrikeInterval);

        // 3. Expiry dates
        var expiryDay = Enum.Parse<DayOfWeek>(underlying.ExpiryDay, ignoreCase: true);
        var expiries = GetNextExpiries(expiryDay, underlying.WeeksAhead).ToArray();

        if (expiries.Length == 0)
        {
            _logger.LogInformation(
                "[InstrumentSync] [{Symbol}] WeeksAhead=0 — skipping sync",
                underlying.Symbol);
            return [];
        }


        _logger.LogInformation(
            "[InstrumentSync] [{Symbol}] Expiries: {Expiries} | Strikes: {Min}–{Max}",
            underlying.Symbol,
            string.Join(", ", expiries),
            minStrike, maxStrike);

        // 4. Fetch option chain from Fyers — one call per expiry for next-week coverage
        var allInstruments = new List<Instrument>();
        var tokenOffset = 0;
        var anyFailed = false;

        foreach (var expiry in expiries)
        {
            var chain = await FetchFyersOptionChainAsync(underlying.Symbol, expiry, ct);

            if (chain.Count > 0)
            {
                var built = BuildFromFyersChain(chain, underlying, new[] { expiry }, minStrike, maxStrike, tokenOffset);
                tokenOffset += built.Count;
                allInstruments.AddRange(built);
                _logger.LogInformation(
                    "[InstrumentSync] [{Symbol}] {Expiry}: {Count} instruments",
                    underlying.Symbol, expiry, built.Count);
            }
            else
            {
                _logger.LogWarning(
                    "[InstrumentSync] [{Symbol}] Fyers chain unavailable for {Expiry} — skipping. " +
                    "Last real instruments will be used. Refresh Fyers token to fix.",
                    underlying.Symbol, expiry);
                anyFailed = true;
            }
        }

        if (allInstruments.Count == 0)
        {
            LastSyncWasSynthetic = true;
            return [];
        }

        _logger.LogInformation(
            "[InstrumentSync] [{Symbol}] Built {Count} instruments from Fyers chain",
            underlying.Symbol, allInstruments.Count);

        return allInstruments;
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

            var client = _httpClientFactory.CreateClient("Fyers");
            client.DefaultRequestHeaders.Remove("Authorization");
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "Authorization",
                $"{_configuration["Fyers:ClientId"]}:{_tokenService.AccessToken}");
            var response = await client.GetAsync(
                $"https://api-t1.fyers.in/data/quotes?symbols={fyersSymbol}", ct);

            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) return 0;

            var json = JsonDocument.Parse(body);
            var d = json.RootElement.GetProperty("d").EnumerateArray().FirstOrDefault();
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
        string underlyingSymbol, DateOnly expiry, CancellationToken ct)
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

            var client = _httpClientFactory.CreateClient("Fyers");
            client.DefaultRequestHeaders.Remove("Authorization");
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "Authorization",
                $"{_configuration["Fyers:ClientId"]}:{_tokenService.AccessToken}");

            // Convert expiry DateOnly to Unix timestamp (midnight IST)
            var expiryIst = expiry.ToDateTime(new TimeOnly(15, 30), DateTimeKind.Unspecified);
            var expiryUtc = TimeZoneInfo.ConvertTimeToUtc(expiryIst, Ist);
            var timestamp = new DateTimeOffset(expiryUtc).ToUnixTimeSeconds();

            _logger.LogInformation(
            "[InstrumentSync] [{Symbol}] Fetching chain for {Expiry} — timestamp={Ts}",
            underlyingSymbol, expiry, timestamp);

            var url = $"https://api-t1.fyers.in/data/options-chain-v3?symbol={chainSymbol}&strikecount=60&timestamp={timestamp}";
            var response = await client.GetAsync(url, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "[InstrumentSync] [{Symbol}] Fyers option chain HTTP {Status} for {Expiry}: {Body}",
                    underlyingSymbol, response.StatusCode, expiry, body);
                return [];
            }

            // Parse once, reuse below
            var json = JsonDocument.Parse(body);

            // Check Fyers application-level error code
            // After the code != 200 check, instead of returning [] immediately:
            if (json.RootElement.TryGetProperty("code", out var code) && code.GetInt32() != 200)
            {
                // Try to extract correct expiry timestamp from Fyers error response
                var correctTimestamp = TryGetCorrectExpiryTimestamp(json, expiry);
                if (correctTimestamp.HasValue)
                {
                    _logger.LogInformation(
                        "[InstrumentSync] [{Symbol}] Retrying with Fyers-provided timestamp {Ts} for {Expiry}",
                        underlyingSymbol, correctTimestamp.Value, expiry);

                    var retryUrl = $"https://api-t1.fyers.in/data/options-chain-v3?symbol={chainSymbol}&strikecount=60&timestamp={correctTimestamp.Value}";
                    var retryResponse = await client.GetAsync(retryUrl, ct);
                    var retryBody = await retryResponse.Content.ReadAsStringAsync(ct);
                    json = JsonDocument.Parse(retryBody);

                    if (!retryResponse.IsSuccessStatusCode ||
                        (json.RootElement.TryGetProperty("code", out var rc) && rc.GetInt32() != 200))
                    {
                        _logger.LogWarning(
                            "[InstrumentSync] [{Symbol}] Retry also failed for {Expiry}: {Body}",
                            underlyingSymbol, expiry, retryBody);
                        return [];
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "[InstrumentSync] [{Symbol}] Fyers option chain error for {Expiry}: {Body}",
                        underlyingSymbol, expiry, body);
                    return [];
                }
            }

            if (!json.RootElement.TryGetProperty("data", out var data)) return [];
            if (!data.TryGetProperty("optionsChain", out var chain)) return [];

            var records = new List<FyersOptionRecord>();
            foreach (var opt in chain.EnumerateArray())
            {
                if (!opt.TryGetProperty("strike_price", out var sp)) continue;
                if (!opt.TryGetProperty("option_type", out var ot)) continue;
                if (!opt.TryGetProperty("symbol", out var sym)) continue;
                if (!opt.TryGetProperty("fyToken", out var tok)) continue;

                var strike = sp.GetDecimal();
                if (strike <= 0) continue;

                records.Add(new FyersOptionRecord(
                    Symbol: sym.GetString() ?? "",
                    FyToken: tok.GetString() ?? "",
                    Strike: strike,
                    OptionType: ot.GetString() ?? "",
                    Ltp: opt.TryGetProperty("ltp", out var ltp) ? ltp.GetDecimal() : 0m));
            }

            _logger.LogInformation(
                "[InstrumentSync] [{Symbol}] Fetched {Count} option records from Fyers for expiry {Expiry}",
                underlyingSymbol, records.Count, expiry);

            return records;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[InstrumentSync] [{Symbol}] Failed to fetch Fyers option chain", underlyingSymbol);
            return [];
        }
    }

    private static long? TryGetCorrectExpiryTimestamp(JsonDocument errorResponse, DateOnly targetExpiry)
    {
        try
        {
            var expiryData = errorResponse.RootElement
                .GetProperty("data")
                .GetProperty("expiryData");

            var targetStr = targetExpiry.ToString("dd-MM-yyyy");

            foreach (var item in expiryData.EnumerateArray())
            {
                if (item.GetProperty("date").GetString() == targetStr)
                    return long.Parse(item.GetProperty("expiry").GetString()!);
            }
        }
        catch { /* ignore */ }
        return null;
    }

    // ── Build instruments from Fyers chain ────────────────────────────────────

    private List<Instrument> BuildFromFyersChain(
        List<FyersOptionRecord> chain,
        UnderlyingConfig underlying,
        DateOnly[] expiries,
        decimal minStrike,
        decimal maxStrike,
        int tokenOffset = 0)
    {
        var instruments = new List<Instrument>();
        var tokenBase = ComputeTokenBase(underlying.Symbol);
        var tokenIndex = tokenOffset; 

        foreach (var record in chain)
        {
            if (record.Strike < minStrike || record.Strike > maxStrike) continue;

            var optionType = record.OptionType.Equals("PE", StringComparison.OrdinalIgnoreCase)
                ? OptionType.Put : OptionType.Call;

            var expiry = ParseExpiryFromFyersSymbol(record.Symbol, underlying.Symbol, expiries);
            if (expiry is null) continue;

            var tradingSymbol = BuildTradingSymbol(
                underlying.Symbol, expiry.Value, optionType, record.Strike);

            instruments.Add(Instrument.Create(
                instrumentToken: tokenBase + tokenIndex++,
                tradingSymbol: tradingSymbol,
                name: underlying.Symbol,
                exchange: underlying.Exchange,
                instrumentType: InstrumentType.FuturesAndOptions,
                lotSize: underlying.LotSize,
                tickSize: 0.05m,
                optionType: optionType,
                strikePrice: record.Strike,
                expiryDate: expiry.Value));
        }

        return instruments;
    }

    private static string BuildTradingSymbol(
    string underlying, DateOnly expiry, OptionType optionType, decimal strike)
    {
        var suffix = optionType == OptionType.Put ? "P" : "C";
        var expiryStr = expiry.ToString("yyMMdd");  // 260519
        return $"{underlying}{expiryStr}{suffix}{(int)strike}";
        // → NIFTY260519C24250
    }
    // ── Build synthetic instruments (fallback) ────────────────────────────────

    private List<Instrument> BuildSyntheticInstruments(
        UnderlyingConfig underlying,
        DateOnly[] expiries,
        decimal minStrike,
        decimal maxStrike)
    {
        var instruments = new List<Instrument>();
        var tokenBase = ComputeTokenBase(underlying.Symbol);
        var tokenIndex = 0;

        foreach (var expiry in expiries)
        {
            var expiryStr = expiry.ToString("yyMMdd"); //eg.

            for (var strike = (int)minStrike; strike <= (int)maxStrike; strike += underlying.StrikeInterval)
            {
                foreach (var (optType, suffix) in new[] { (OptionType.Put, "PE"), (OptionType.Call, "CE") })
                {
                    instruments.Add(Instrument.Create(
                        instrumentToken: tokenBase + tokenIndex++,
                        tradingSymbol: $"{underlying.Symbol}{expiryStr}{strike}{suffix}",
                        name: underlying.Symbol,
                        exchange: underlying.Exchange,
                        instrumentType: InstrumentType.FuturesAndOptions,
                        lotSize: underlying.LotSize,
                        tickSize: 0.05m,
                        optionType: optType,
                        strikePrice: strike,
                        expiryDate: expiry));
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
        var today = DateOnly.FromDateTime(DateTime.Today);
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
    /// NIFTY → 10_000_000, FINNIFTY → 20_000_000, others get a hash-derived bucket.
    /// </summary>
    private static int ComputeTokenBase(string underlyingSymbol)
    {
        return underlyingSymbol.ToUpperInvariant() switch
        {
            "NIFTY" => 10_000_000,
            "FINNIFTY" => 20_000_000,
            _ => (Math.Abs(underlyingSymbol.GetHashCode()) % 89 + 1) * 1_000_000
        };
    }

    /// <summary>
    /// Attempts to match a Fyers option symbol against the known expiry dates.
    ///
    /// Fyers short-date format for weeklies (both NIFTY and FINNIFTY):
    ///   {UNDERLYING}{YY}{M}{DD}  e.g. NIFTY26512  or  FINNIFTY26514
    /// where M is the single-digit month (no leading zero) and DD is zero-padded day.
    ///
    /// Falls back to the nearest expiry if no pattern matches (handles edge cases
    /// where Fyers uses a different encoding for monthly expiries).
    /// </summary>
    private static DateOnly? ParseExpiryFromFyersSymbol(
        string symbol,
        string underlyingName,
        DateOnly[] knownExpiries)
    {
        foreach (var expiry in knownExpiries)
        {
            var yy = expiry.Year.ToString()[2..];    // "26"
            var m = expiry.Month.ToString();         // "5"  (no leading zero)
            var dd = expiry.Day.ToString("D2");       // "12"
            var pattern = $"{underlyingName.ToUpperInvariant()}{yy}{m}{dd}"; // "NIFTY26512"

            if (symbol.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return expiry;
        }

        // Fallback: assign to nearest expiry rather than silently dropping the record
        return knownExpiries.Length > 0 ? knownExpiries[0] : null;
    }

    // ── Private records ───────────────────────────────────────────────────────

    private record FyersOptionRecord(
        string Symbol,
        string FyToken,
        decimal Strike,
        string OptionType,
        decimal Ltp);
}

// ── Configuration model ───────────────────────────────────────────────────────

/// <summary>
/// Mirrors one entry under <c>InstrumentSync:Underlyings</c> in appsettings.json.
/// </summary>
public sealed class UnderlyingConfig
{
    /// <summary>e.g. "NIFTY" or "FINNIFTY"</summary>
    public string Symbol { get; init; } = "";

    /// <summary>Human-readable spot symbol, e.g. "NIFTY 50" or "NIFTY BANK".</summary>
    public string SpotSymbol { get; init; } = "";

    /// <summary>Options exchange, e.g. "NFO".</summary>
    public string Exchange { get; init; } = "NFO";

    /// <summary>Strike rounding interval: 50 for NIFTY, 100 for FINNIFTY.</summary>
    public int StrikeInterval { get; init; } = 50;

    /// <summary>Lot size for this underlying.</summary>
    public int LotSize { get; init; } = 25;

    /// <summary>Day-of-week string for weekly expiry, e.g. "Tuesday" or "Wednesday".</summary>
    public string ExpiryDay { get; init; } = "Tuesday";

    /// <summary>How many successive weekly expiries to sync (default 2).</summary>
    public int WeeksAhead { get; init; } = 2;

    /// <summary>
    /// Fallback spot used when the live feed is unavailable.
    /// Set conservatively high so the strike range is always wide enough.
    /// </summary>
    public decimal DefaultSpot { get; init; } = 24_000m;
}

