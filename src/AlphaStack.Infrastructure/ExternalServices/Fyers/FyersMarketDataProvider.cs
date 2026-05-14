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

    private readonly Dictionary<string, (DateTime fetchedAt, JsonDocument data)> _chainCache = new();

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
        // Parse strike and option type from our DB symbol e.g. NIFTY25APR23950PE
        var (strike, optionType) = ParseOptionSymbol(tradingSymbol);
        var chainSymbol = tradingSymbol.StartsWith(
            "BANKNIFTY",
            StringComparison.OrdinalIgnoreCase)
            ? "NSE%3ANIFTYBANK-INDEX"
            : "NSE%3ANIFTY50-INDEX";

        // Create client BEFORE cache check
        var client = _httpClientFactory.CreateClient("Fyers");

        client.DefaultRequestHeaders.Remove("Authorization");

        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization",
            $"{_configuration["Fyers:ClientId"]}:{_tokenService.AccessToken}");

        var absoluteUrl =
            $"https://api-t1.fyers.in/data/options-chain-v3?symbol={chainSymbol}&strikecount=20&timestamp=";

        JsonDocument json;

        // Cache valid for 5 seconds
        if (_chainCache.TryGetValue(chainSymbol, out var cached) &&
            DateTime.UtcNow - cached.fetchedAt < TimeSpan.FromSeconds(5))
        {
            json = cached.data;

            _logger.LogInformation(
                "[FYERS] Using cached option chain for {Symbol}",
                chainSymbol);
        }
        else
        {
            _logger.LogInformation(
                "[FYERS] Fetching fresh option chain for {Symbol}",
                chainSymbol);

            var response = await client.GetAsync(absoluteUrl, ct);

            var body = await response.Content.ReadAsStringAsync(ct);

            EnsureSuccess(response, body, "OptionChain");

            json = JsonDocument.Parse(body);

            EnsureOk(json.RootElement, "OptionChain");

            _chainCache[chainSymbol] =
                (DateTime.UtcNow, json);
        }

        var options = json.RootElement
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
            var oi = opt.TryGetProperty("oi", out var oiEl) ? oiEl.GetDecimal() : 0m;
            var vol = opt.TryGetProperty("volume", out var volEl) ? volEl.GetInt64() : 0L;

            _logger.LogInformation("[FYERS] Option {Symbol} strike={Strike} type={Type} LTP={LTP}",
                tradingSymbol, strike, optionType, ltp);

            return new Quote(
                TradingSymbol: tradingSymbol,
                Exchange: "NFO",
                LastPrice: ltp,
                BidPrice: opt.TryGetProperty("bid", out var bid) ? bid.GetDecimal() : 0m,
                AskPrice: opt.TryGetProperty("ask", out var ask) ? ask.GetDecimal() : 0m,
                OpenPrice: 0,
                HighPrice: 0,
                LowPrice: 0,
                ClosePrice: 0,
                Volume: vol,
                OpenInterest: oi,
                Timestamp: DateTime.UtcNow);
        }

        _logger.LogWarning("[FYERS] Option {Symbol} strike={Strike} {Type} not found in chain",
            tradingSymbol, strike, optionType);
        return null;
    }

    private static (decimal strike, string optionType) ParseOptionSymbol(string symbol)
    {
        // Supports:
        // NIFTY260519C24000
        // NIFTY260519P23800
        // BANKNIFTY260520C53600
        // BANKNIFTY260520P53200

        var match = Regex.Match(
            symbol,
            @"(?:\d{6})(C|P|CE|PE)(\d+)$",
            RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            return (0m, string.Empty);
        }

        var rawType = match.Groups[1].Value.ToUpperInvariant();
        var optionType = rawType switch
        {
            "C" => "CE",
            "P" => "PE",
            _ => rawType
        };

        if (!decimal.TryParse(match.Groups[2].Value, out var strike))
        {
            return (0m, optionType);
        }

        return (strike, optionType);
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
            260105 => "NSE:NIFTYBANK-INDEX",
            260009 => "NSE:NIFTYBANK-INDEX",  // ← add this
            26009  => "NSE:NIFTYBANK-INDEX",  // ← add this
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
