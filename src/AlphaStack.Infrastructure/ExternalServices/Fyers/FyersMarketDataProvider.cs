using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using AlphaStack.Application.Common.Interfaces;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace AlphaStack.Infrastructure.ExternalServices.Fyers;

public class FyersMarketDataProvider : IMarketDataProvider
{
    private static readonly TimeSpan OptionChainCacheTtl = TimeSpan.FromSeconds(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IInstrumentRepository _instruments;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FyersMarketDataProvider> _logger;
    private Dictionary<string, string>? _symbolMaster;
    private readonly FyersTokenService _tokenService;
    private readonly SemaphoreSlim _chainFetchLock = new(1, 1);
    private readonly Dictionary<string, (DateTime fetchedAt, JsonElement data)> _chainCache = new();

    // Populated on first chain fetch — maps expiry DateOnly → Fyers Unix timestamp string.
    // Fyers requires the exact timestamp value from their expiryData list; arbitrary values
    // (e.g. SOD) return "Please provide valid expiry". This cache avoids a round-trip on
    // every chain lookup while staying fresh across token refreshes.
    private readonly Dictionary<DateOnly, long> _expiryTimestampCache = new();
    private DateTime _expiryTimestampCacheFetchedAt = DateTime.MinValue;
    private static readonly TimeSpan ExpiryTimestampCacheTtl = TimeSpan.FromHours(1);

    public FyersMarketDataProvider(
        IHttpClientFactory httpClientFactory,
        IInstrumentRepository instruments,
        IConfiguration configuration,
        FyersTokenService tokenService,
        ILogger<FyersMarketDataProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _instruments = instruments;
        _configuration = configuration;
        _tokenService = tokenService;
        _logger = logger;
        
    }

    public async Task<Quote?> GetQuoteAsync(
    string symbol,
    string exchange,
    CancellationToken ct = default)
    {
        var fyersSymbol = await ResolveSymbolAsync(symbol, exchange, ct);
        if (string.IsNullOrWhiteSpace(fyersSymbol))
        {
            _logger.LogWarning("[FYERS] Symbol not resolved for {Exchange}:{Symbol}", exchange, symbol);
            return null;
        }

        // For NFO options use option chain API
        if (exchange.Equals("NFO", StringComparison.OrdinalIgnoreCase))
            return await GetOptionQuoteFromChainAsync(symbol, ct);

        // For NSE indices use quotes API
        var client = _httpClientFactory.CreateClient("Fyers");
        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization",
            $"{_configuration["Fyers:ClientId"]}:{_tokenService.AccessToken}");
        var absoluteUrl = $"https://api-t1.fyers.in/data/quotes?symbols={Uri.EscapeDataString(fyersSymbol)}";

        _logger.LogInformation("[FYERS] Calling: {Url}", absoluteUrl);
        var response = await client.GetAsync(absoluteUrl, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        _logger.LogInformation("[FYERS] Response {Status}: {Body}", response.StatusCode, body);
        EnsureSuccess(response, body, "GetQuote");

        using var json = JsonDocument.Parse(body);
        EnsureOk(json.RootElement, "GetQuote");

        var quotePayload = json.RootElement
            .GetProperty("d")
            .EnumerateArray()
            .FirstOrDefault();

        if (quotePayload.ValueKind == JsonValueKind.Undefined
            || !quotePayload.TryGetProperty("v", out var v))
        {
            _logger.LogWarning("[FYERS] Quote missing for {Symbol}: {Body}", fyersSymbol, body);
            return null;
        }

        return new Quote(
            TradingSymbol: symbol,
            Exchange: exchange,
            LastPrice: GetDecimalOrDefault(v, "lp"),
            BidPrice: GetDecimalOrDefault(v, "bid"),
            AskPrice: GetDecimalOrDefault(v, "ask"),
            OpenPrice: GetDecimalOrDefault(v, "open_price"),
            HighPrice: GetDecimalOrDefault(v, "high_price"),
            LowPrice: GetDecimalOrDefault(v, "low_price"),
            ClosePrice: GetDecimalOrDefault(v, "prev_close_price"),
            Volume: GetInt64OrDefault(v, "volume"),
            OpenInterest: GetDecimalOrDefault(v, "oi"),
            Timestamp: GetTimestampOrUtcNow(v));
    }

    private async Task<Quote?> GetOptionQuoteFromChainAsync(string tradingSymbol, CancellationToken ct)
    {
        var (strike, optionType) = ParseOptionSymbol(tradingSymbol);
        if (strike == 0 || string.IsNullOrEmpty(optionType))
        {
            _logger.LogWarning("[FYERS] Could not parse symbol {Symbol}", tradingSymbol);
            return null;
        }

        // Parse the target expiry from our internal symbol format (e.g. NIFTY260616C23750 -> 2026-06-16).
        // We then look up the exact Fyers timestamp for that expiry and pass it in the chain URL.
        // Without this, Fyers returns the NEAREST expiry chain (today's expiring options on expiry day),
        // causing near-zero LTPs for next-week strikes.
        // Verified: timestamp=1781604000 returns June 16 chain with correct LTPs (~32-57 range).
        //           timestamp= (empty) returns June 9 chain on expiry day with LTPs ~0.25.
        var targetExpiry = ParseExpiryFromInternalSymbol(tradingSymbol);
        var expiryTimestamp = targetExpiry.HasValue
            ? await GetFyersExpiryTimestampAsync(targetExpiry.Value, tradingSymbol.StartsWith("FINNIFTY", StringComparison.OrdinalIgnoreCase), ct)
            : null;

        var chainSymbol = tradingSymbol.StartsWith("FINNIFTY", StringComparison.OrdinalIgnoreCase)
            ? "NSE%3AFINNIFTY-INDEX"
            : "NSE%3ANIFTY50-INDEX";

        JsonElement? chain;
        try
        {
            chain = await GetChainAsync(chainSymbol, expiryTimestamp, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[FYERS] Chain fetch failed for {Symbol}: {Msg}", chainSymbol, ex.Message);
            return null;
        }

        if (chain is null) return null;

        var options = chain.Value
            .GetProperty("data")
            .GetProperty("optionsChain")
            .EnumerateArray();

        foreach (var opt in options)
        {
            if (!opt.TryGetProperty("strike_price", out var sp)) continue;
            if (!opt.TryGetProperty("option_type", out var ot)) continue;
            if (sp.GetDecimal() != strike) continue;
            if (!ot.GetString()!.Equals(optionType, StringComparison.OrdinalIgnoreCase)) continue;

            var ltp = opt.TryGetProperty("ltp", out var ltpEl) ? ltpEl.GetDecimal() : 0m;
            var oi  = opt.TryGetProperty("oi",  out var oiEl)  ? oiEl.GetDecimal()  : 0m;
            var vol = opt.TryGetProperty("volume", out var volEl) ? volEl.GetInt64() : 0L;

            _logger.LogInformation("[FYERS] Option {Symbol} strike={Strike} type={Type} LTP={LTP}",
                tradingSymbol, strike, optionType, ltp);

            return new Quote(
                TradingSymbol: tradingSymbol,
                Exchange: "NFO",
                LastPrice: ltp,
                BidPrice: opt.TryGetProperty("bid", out var bid) ? bid.GetDecimal() : 0m,
                AskPrice: opt.TryGetProperty("ask", out var ask) ? ask.GetDecimal() : 0m,
                OpenPrice: 0, HighPrice: 0, LowPrice: 0, ClosePrice: 0,
                Volume: vol,
                OpenInterest: oi,
                Timestamp: DateTime.UtcNow);
        }

        _logger.LogWarning("[FYERS] Option {Symbol} strike={Strike} {Type} expiry={Expiry} not found in chain",
            tradingSymbol, strike, optionType, targetExpiry);
        return null;
    }

    /// <summary>
    /// Returns the exact Fyers expiry timestamp for a given DateOnly by consulting the
    /// expiryData list that Fyers includes in every chain response.
    /// Fyers rejects arbitrary timestamps — only values from their own list are valid.
    /// Cache is refreshed hourly (expiry list changes slowly).
    /// </summary>
    private async Task<long?> GetFyersExpiryTimestampAsync(DateOnly expiry, bool isFinnifty, CancellationToken ct)
    {
        await _chainFetchLock.WaitAsync(ct);
        try
        {
            // Refresh expiry timestamp cache if stale
            if (DateTime.UtcNow - _expiryTimestampCacheFetchedAt > ExpiryTimestampCacheTtl)
            {
                var chainSymbol = isFinnifty ? "NSE%3AFINNIFTY-INDEX" : "NSE%3ANIFTY50-INDEX";
                var client = _httpClientFactory.CreateClient("Fyers");
                client.DefaultRequestHeaders.Remove("Authorization");
                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    "Authorization",
                    $"{_configuration["Fyers:ClientId"]}:{_tokenService.AccessToken}");

                // Empty timestamp returns nearest expiry chain but ALWAYS includes full expiryData list
                var url = $"https://api-t1.fyers.in/data/options-chain-v3?symbol={chainSymbol}&strikecount=1&timestamp=";
                var response = await client.GetAsync(url, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                var root = JsonDocument.Parse(body).RootElement.Clone();

                _expiryTimestampCache.Clear();
                if (root.TryGetProperty("data", out var dataEl) &&
                    dataEl.TryGetProperty("expiryData", out var expiryDataEl))
                {
                    foreach (var item in expiryDataEl.EnumerateArray())
                    {
                        // date format: "16-06-2026"
                        if (item.TryGetProperty("date", out var dateEl) &&
                            item.TryGetProperty("expiry", out var tsEl) &&
                            DateOnly.TryParseExact(dateEl.GetString(), "dd-MM-yyyy",
                                CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) &&
                            long.TryParse(tsEl.GetString(), out var ts))
                        {
                            _expiryTimestampCache[d] = ts;
                        }
                    }
                }

                _expiryTimestampCacheFetchedAt = DateTime.UtcNow;
                _logger.LogInformation("[FYERS] Loaded {Count} expiry timestamps from Fyers", _expiryTimestampCache.Count);
            }
        }
        finally
        {
            _chainFetchLock.Release();
        }

        if (_expiryTimestampCache.TryGetValue(expiry, out var timestamp))
            return timestamp;

        _logger.LogWarning("[FYERS] No Fyers timestamp found for expiry {Expiry} — falling back to nearest chain", expiry);
        return null;
    }

    private async Task<JsonElement?> GetChainAsync(string chainSymbol, long? expiryTimestamp, CancellationToken ct)
    {
        // Include expiry timestamp in cache key so different expiries are cached independently
        var cacheKey = expiryTimestamp.HasValue ? $"{chainSymbol}:{expiryTimestamp}" : chainSymbol;

        // Fast path — cache hit (no lock needed for reads)
        if (_chainCache.TryGetValue(cacheKey, out var cached) &&
            DateTime.UtcNow - cached.fetchedAt < TimeSpan.FromSeconds(30))
        {
            return cached.data;
        }

        await _chainFetchLock.WaitAsync(ct);
        try
        {
            // Double-check inside lock
            if (_chainCache.TryGetValue(cacheKey, out cached) &&
                DateTime.UtcNow - cached.fetchedAt < TimeSpan.FromSeconds(30))
            {
                return cached.data;
            }

            var tsParam = expiryTimestamp.HasValue
                ? expiryTimestamp.Value.ToString(CultureInfo.InvariantCulture)
                : string.Empty;

            _logger.LogInformation("[FYERS] Fetching fresh option chain for {Symbol} expiry={Expiry}",
                chainSymbol, tsParam == string.Empty ? "nearest" : tsParam);

            var client = _httpClientFactory.CreateClient("Fyers");
            client.DefaultRequestHeaders.Remove("Authorization");
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "Authorization",
                $"{_configuration["Fyers:ClientId"]}:{_tokenService.AccessToken}");

            var url = $"https://api-t1.fyers.in/data/options-chain-v3?symbol={chainSymbol}&strikecount=60&timestamp={tsParam}";
            var response = await client.GetAsync(url, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            EnsureSuccess(response, body, "OptionChain");

            var root = JsonDocument.Parse(body).RootElement.Clone();
            EnsureOk(root, "OptionChain");

            _chainCache[cacheKey] = (DateTime.UtcNow, root);
            return root;
        }
        finally
        {
            _chainFetchLock.Release();
        }
    }

    /// <summary>
    /// Extracts expiry DateOnly from internal symbol format: {UNDERLYING}{yyMMdd}{C|P}{strike}
    /// e.g. NIFTY260616C23750 -> 2026-06-16. Returns null for Fyers native format symbols.
    /// </summary>
    private static DateOnly? ParseExpiryFromInternalSymbol(string symbol)
    {
        var match = Regex.Match(symbol, @"^[A-Z]+(\d{6})(C|P)\d+$", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        return DateOnly.TryParseExact(match.Groups[1].Value, "yyMMdd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry)
            ? expiry : null;
    }

    private static (decimal strike, string optionType) ParseOptionSymbol(string symbol)
    {
        // Format 1 (Fyers): NIFTY26MAY2624050CE  → ddMMMyy + CE/PE
        var match = Regex.Match(symbol,
            @"^[A-Z]+(\d{2}[A-Z]{3}\d{2})(\d+)(CE|PE)$",
            RegexOptions.IgnoreCase);

        if (match.Success)
        {
            var optionType = match.Groups[3].Value.ToUpperInvariant();
            if (decimal.TryParse(match.Groups[2].Value, out var s1))
                return (s1, optionType);
        }

        // Format 2 (internal): NIFTY260526C24050 → yyMMdd + C/P + strike
        match = Regex.Match(symbol,
            @"^[A-Z]+\d{6}(C|P)(\d+)$",
            RegexOptions.IgnoreCase);

        if (match.Success)
        {
            var rawType    = match.Groups[1].Value.ToUpperInvariant();
            var optionType = rawType == "C" ? "CE" : "PE";
            if (decimal.TryParse(match.Groups[2].Value, out var s2))
                return (s2, optionType);
        }

        return (0m, string.Empty);
    }
    public async Task<List<Candle>> GetHistoricalDataAsync(
        int instrumentToken,
        string interval,
        DateTime from,
        DateTime to,
        CancellationToken ct = default)
    {
        var fyersSymbol = instrumentToken switch
        {
            256265 => "NSE:NIFTY50-INDEX",
            257801 => "NSE:FINNIFTY-INDEX",
            _ => _configuration[$"Fyers:Historical:{instrumentToken}:Symbol"]
                ?? "NSE:NIFTY50-INDEX"
        };

        var query = string.Join("&", new[]
        {
            $"symbol={Uri.EscapeDataString(fyersSymbol)}",
            $"resolution={Uri.EscapeDataString(ToFyersResolution(interval))}",
            "date_format=1",
            $"range_from={Uri.EscapeDataString(from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}",
            $"range_to={Uri.EscapeDataString(to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}",
            "cont_flag=1"
        });

        var client = _httpClientFactory.CreateClient("FyersData");
        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization",
            $"{_configuration["Fyers:ClientId"]}:{_tokenService.AccessToken}");
        var absoluteUrl = $"https://api-t1.fyers.in/data/history?{query}";
        var response = await client.GetAsync(absoluteUrl, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        EnsureSuccess(response, body, "GetHistoricalData");

        using var json = JsonDocument.Parse(body);
        EnsureOk(json.RootElement, "GetHistoricalData");

        if (!json.RootElement.TryGetProperty("candles", out var candles)
            || candles.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return candles.EnumerateArray()
            .Where(c => c.ValueKind == JsonValueKind.Array && c.GetArrayLength() >= 6)
            .Select(c => new Candle(
                Timestamp: DateTimeOffset.FromUnixTimeSeconds(c[0].GetInt64()).UtcDateTime,
                Open: c[1].GetDecimal(),
                High: c[2].GetDecimal(),
                Low: c[3].GetDecimal(),
                Close: c[4].GetDecimal(),
                Volume: c[5].GetInt64()))
            .ToList();
    }

    private async Task<string?> ResolveSymbolAsync(string symbol, string exchange, CancellationToken ct)
    {
        var key = $"{exchange}:{symbol}";
        var configured = _configuration[$"Fyers:Symbols:{key}"]
            ?? _configuration[$"Fyers:Symbols:{symbol}"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        if (exchange.Equals("NSE", StringComparison.OrdinalIgnoreCase))
        {
            if (symbol.Equals("NIFTY 50", StringComparison.OrdinalIgnoreCase)
                || symbol.Equals("NIFTY", StringComparison.OrdinalIgnoreCase))
            {
                return "NSE:NIFTY50-INDEX";
            }

            if (symbol.Equals("INDIA VIX", StringComparison.OrdinalIgnoreCase))
                return "NSE:INDIA_VIX-INDEX";  // try underscore instead of no space
        }

        var master = await LoadSymbolMasterAsync(ct);
        if (master.TryGetValue(Normalize(symbol), out var fyersSymbol))
            return fyersSymbol;

        var instrument = await _instruments.GetBySymbolAndExchangeAsync(symbol, exchange, ct);
        if (instrument is not null && master.TryGetValue(Normalize(instrument.TradingSymbol), out fyersSymbol))
            return fyersSymbol;

        return ToFallbackSymbol(symbol, exchange);
    }

    private async Task<Dictionary<string, string>> LoadSymbolMasterAsync(CancellationToken ct)
    {
        if (_symbolMaster is not null)
            return _symbolMaster;

        var urls = _configuration
            .GetSection("Fyers:SymbolMasterUrls")
            .Get<string[]>()
            ?? [];

        var symbols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in urls.Where(u => !string.IsNullOrWhiteSpace(u)))
        {
            try
            {
                var client = _httpClientFactory.CreateClient("Fyers");
                var csv = await client.GetStringAsync(url, ct);
                foreach (var item in ParseSymbolMaster(csv))
                    symbols.TryAdd(item.Key, item.Value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[FYERS] Failed to load symbol master {Url}", url);
            }
        }

        _symbolMaster = symbols;
        return _symbolMaster;
    }

    private static Dictionary<string, string> ParseSymbolMaster(string csv)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            var cols = SplitCsvLine(line);
            foreach (var value in cols.Where(c => c.Contains(':', StringComparison.Ordinal)))
            {
                var fyersSymbol = value.Trim();
                result.TryAdd(Normalize(fyersSymbol.Split(':').Last()), fyersSymbol);
            }
        }

        return result;
    }

    private static string ToFallbackSymbol(string symbol, string exchange)
    {
        var normalized = symbol.Replace(" ", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
        return exchange.ToUpperInvariant() switch
        {
            "NFO" => $"NSE:{normalized}",
            "NSE" => $"NSE:{normalized}-EQ",
            "BSE" => $"BSE:{normalized}",
            _ => $"{exchange.ToUpperInvariant()}:{normalized}"
        };
    }

    private static string ToFyersResolution(string interval)
    {
        var normalized = interval.Trim().ToLowerInvariant();
        if (normalized is "day" or "daily" or "1d" or "d")
            return "D";

        normalized = normalized
            .Replace("minute", "", StringComparison.Ordinal)
            .Replace("min", "", StringComparison.Ordinal);

        return int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            ? minutes.ToString(CultureInfo.InvariantCulture)
            : "D";
    }

    private static DateTime GetTimestampOrUtcNow(JsonElement value)
    {
        var seconds = GetInt64OrDefault(value, "tt");
        return seconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : DateTime.UtcNow;
    }

    private static decimal GetDecimalOrDefault(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return 0m;

        return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var result)
            ? result
            : 0m;
    }

    private static long GetInt64OrDefault(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return 0;

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result)
            ? result
            : 0;
    }

    private static void EnsureSuccess(HttpResponseMessage response, string body, string operation)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"FYERS {operation} failed ({response.StatusCode}): {body}");
    }

    private static void EnsureOk(JsonElement root, string operation)
    {
        var status = root.TryGetProperty("s", out var s) ? s.GetString() : null;
        if (status is not null && !status.Equals("ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"FYERS {operation} failed: {root}");
    }

    private static List<string> SplitCsvLine(string line)
    {
        var values = new List<string>();
        var current = new List<char>();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Add('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (ch == ',' && !inQuotes)
            {
                values.Add(new string(current.ToArray()));
                current.Clear();
            }
            else
            {
                current.Add(ch);
            }
        }

        values.Add(new string(current.ToArray()));
        return values;
    }

    private static string Normalize(string value)
    {
        return value.Trim().Replace(" ", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
    }
}