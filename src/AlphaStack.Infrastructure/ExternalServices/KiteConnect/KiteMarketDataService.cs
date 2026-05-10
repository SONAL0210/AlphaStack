using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Domain.Entities;
using AlphaStack.Domain.Enums;

namespace AlphaStack.Infrastructure.ExternalServices.KiteConnect;

public class KiteMarketDataService : IKiteMarketDataService, IMarketDataProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IUserProfileRepository _userRepo;
    private readonly IEncryptionService _encryption;
    private readonly IConfiguration _configuration;
    private readonly ILogger<KiteMarketDataService> _logger;

    public KiteMarketDataService(
        IHttpClientFactory httpClientFactory,
        IUserProfileRepository userRepo,
        IEncryptionService encryption,
        IConfiguration configuration,
        ILogger<KiteMarketDataService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _userRepo = userRepo;
        _encryption = encryption;
        _configuration = configuration;
        _logger = logger;
    }

    // ── Quotes ────────────────────────────────────────────────────────────────

    public async Task<Quote> GetQuoteAsync(
        string userProfileId,
        string tradingSymbol,
        string exchange,
        CancellationToken ct = default)
    {
        var quotes = await GetQuotesAsync(userProfileId, [$"{exchange}:{tradingSymbol}"], ct);
        return quotes.FirstOrDefault()
            ?? throw new InvalidOperationException($"No quote returned for {exchange}:{tradingSymbol}");
    }

    public async Task<Quote?> GetQuoteAsync(
        string symbol,
        string exchange,
        CancellationToken ct = default)
    {
        var userProfileId = GetMarketDataUserProfileId();
        return await GetQuoteAsync(userProfileId, symbol, exchange, ct);
    }

    public async Task<IReadOnlyList<Quote>> GetQuotesAsync(
        string userProfileId,
        IEnumerable<string> symbols,
        CancellationToken ct = default)
    {
        // symbols format: ["NSE:INFY", "NFO:NIFTY24DEC24000CE"]
        var symbolList = symbols.ToList();
        var client = await GetAuthenticatedClientAsync(userProfileId, ct);

        var query = string.Join("&", symbolList.Select(s => $"i={Uri.EscapeDataString(s)}"));
        var response = await client.GetAsync($"/quote?{query}", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        EnsureSuccess(response, body, "GetQuotes");

        var json = JsonDocument.Parse(body);
        var data = json.RootElement.GetProperty("data");

        var quotes = new List<Quote>();
        foreach (var symbol in symbolList)
        {
            if (!data.TryGetProperty(symbol, out var q)) continue;

            quotes.Add(new Quote(
                TradingSymbol: symbol.Split(':').Last(),
                Exchange: symbol.Split(':').First(),
                LastPrice: q.GetProperty("last_price").GetDecimal(),
                BidPrice: GetDecimalOrDefault(q, "depth.buy.0.price"),
                AskPrice: GetDecimalOrDefault(q, "depth.sell.0.price"),
                OpenPrice: q.GetProperty("ohlc").GetProperty("open").GetDecimal(),
                HighPrice: q.GetProperty("ohlc").GetProperty("high").GetDecimal(),
                LowPrice: q.GetProperty("ohlc").GetProperty("low").GetDecimal(),
                ClosePrice: q.GetProperty("ohlc").GetProperty("close").GetDecimal(),
                Volume: q.GetProperty("volume").GetInt64(),
                OpenInterest: GetDecimalOrDefault(q, "oi"),
                Timestamp: DateTime.UtcNow
            ));
        }

        return quotes;
    }

    // ── Historical Data ───────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Candle>> GetHistoricalDataAsync(
        string userProfileId,
        int instrumentToken,
        string interval,       // "day", "60minute", "15minute", etc.
        DateTime from,
        DateTime to,
        CancellationToken ct = default)
    {
        var client = await GetAuthenticatedClientAsync(userProfileId, ct);

        var fromStr = from.ToString("yyyy-MM-dd HH:mm:ss");
        var toStr = to.ToString("yyyy-MM-dd HH:mm:ss");

        var url = $"/instruments/historical/{instrumentToken}/{interval}" +
                  $"?from={Uri.EscapeDataString(fromStr)}&to={Uri.EscapeDataString(toStr)}";

        var response = await client.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        EnsureSuccess(response, body, "GetHistoricalData");

        var json = JsonDocument.Parse(body);
        var candles = json.RootElement
            .GetProperty("data")
            .GetProperty("candles")
            .EnumerateArray()
            .Select(c => new Candle(
                Timestamp: DateTime.Parse(c[0].GetString()!),
                Open: c[1].GetDecimal(),
                High: c[2].GetDecimal(),
                Low: c[3].GetDecimal(),
                Close: c[4].GetDecimal(),
                Volume: c[5].GetInt64()
            ))
            .ToList();

        _logger.LogDebug(
            "Fetched {Count} candles for token {Token} ({Interval})",
            candles.Count, instrumentToken, interval);

        return candles;
    }

    public async Task<List<Candle>> GetHistoricalDataAsync(
        int instrumentToken,
        string interval,
        DateTime from,
        DateTime to,
        CancellationToken ct = default)
    {
        var userProfileId = GetMarketDataUserProfileId();
        var candles = await GetHistoricalDataAsync(userProfileId, instrumentToken, interval, from, to, ct);
        return candles.ToList();
    }

    // ── Instrument Master ─────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(
        string exchange,
        CancellationToken ct = default)
    {
        // Instrument master is a CSV download — no auth needed
        var client = _httpClientFactory.CreateClient("KiteConnect");
        var response = await client.GetAsync($"/instruments/{exchange}", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        EnsureSuccess(response, body, "GetInstruments");

        var instruments = new List<Instrument>();
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Skip header row
        foreach (var line in lines.Skip(1))
        {
            var cols = line.Split(',');
            if (cols.Length < 12) continue;

            try
            {
                var instrumentType = ParseInstrumentType(cols[9].Trim());
                OptionType? optionType = cols[8].Trim() switch
                {
                    "CE" => OptionType.Call,
                    "PE" => OptionType.Put,
                    _ => null
                };

                DateOnly? expiry = string.IsNullOrWhiteSpace(cols[5].Trim())
                    ? null
                    : DateOnly.Parse(cols[5].Trim());

                decimal? strike = decimal.TryParse(cols[6].Trim(), out var s) ? s : null;

                instruments.Add(Instrument.Create(
                    instrumentToken: int.Parse(cols[0].Trim()),
                    tradingSymbol: cols[2].Trim(),
                    name: cols[13].Trim(),
                    exchange: cols[11].Trim(),
                    instrumentType: instrumentType,
                    lotSize: decimal.TryParse(cols[10].Trim(), out var lot) ? lot : 1,
                    tickSize: decimal.TryParse(cols[7].Trim(), out var tick) ? tick : 0.05m,
                    optionType: optionType,
                    strikePrice: strike,
                    expiryDate: expiry
                ));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse instrument line: {Line}", line);
            }
        }

        _logger.LogInformation(
            "Fetched {Count} instruments for exchange {Exchange}", instruments.Count, exchange);

        return instruments;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<HttpClient> GetAuthenticatedClientAsync(
        string userProfileId,
        CancellationToken ct)
    {
        var userId = Guid.Parse(userProfileId);
        var user = await _userRepo.GetByIdAsync(userId, ct)
            ?? throw new InvalidOperationException($"UserProfile {userProfileId} not found.");

        if (!user.IsKiteSessionValid())
            throw new KiteAuthException(
                $"Kite session expired or missing for user {user.Username}. Please re-authenticate.");

        var apiKey = _encryption.Decrypt(user.EncryptedKiteApiKey);

        var client = _httpClientFactory.CreateClient("KiteConnect");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("token", $"{apiKey}:{user.KiteAccessToken}");

        return client;
    }

    private string GetMarketDataUserProfileId()
    {
        return _configuration["KiteConnect:MarketDataUserProfileId"]
            ?? throw new InvalidOperationException(
                "Kite market data requires KiteConnect:MarketDataUserProfileId when used through IMarketDataProvider.");
    }

    private static void EnsureSuccess(HttpResponseMessage response, string body, string operation)
    {
        if (!response.IsSuccessStatusCode)
            throw new KiteApiException(
                $"Kite {operation} failed ({response.StatusCode}): {body}");
    }

    private static decimal GetDecimalOrDefault(JsonElement el, string dotPath)
    {
        try
        {
            var parts = dotPath.Split('.');
            var current = el;
            foreach (var part in parts)
            {
                if (int.TryParse(part, out var idx))
                {
                    current = current.EnumerateArray().ElementAtOrDefault(idx);
                }
                else if (!current.TryGetProperty(part, out current))
                {
                    return 0m;
                }
            }
            return current.ValueKind == JsonValueKind.Number ? current.GetDecimal() : 0m;
        }
        catch { return 0m; }
    }

    private static InstrumentType ParseInstrumentType(string raw) => raw switch
    {
        "EQ" => InstrumentType.Equity,
        "FUT" or "CE" or "PE" => InstrumentType.FuturesAndOptions,
        "CUR" => InstrumentType.Currency,
        _ => InstrumentType.Equity
    };
}

public class KiteApiException : Exception
{
    public KiteApiException(string message) : base(message) { }
}
