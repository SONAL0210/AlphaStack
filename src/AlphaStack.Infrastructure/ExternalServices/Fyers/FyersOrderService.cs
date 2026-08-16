using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using AlphaStack.Application.Common.Interfaces;

namespace AlphaStack.Infrastructure.ExternalServices.Fyers;

/// <summary>
/// Fyers order-placement and account-state client. First piece of live order
/// code in the codebase — FyersMarketDataProvider only ever did quotes/history.
/// Built as a separate service so read-only account calls (GetFundsAsync) can
/// be tested against the real account with zero risk before any order-
/// placement method is added to this class.
///
/// Funds response schema verified against a real Fyers v3 /funds call
/// (Aug 2026) — NOT from documentation. Title strings below are exact:
/// "Total Balance", "Utilized Amount", "Available Balance", plus others
/// (Clear Balance, Realized P&L, Collaterals, Fund Transfer, Receivables,
/// Adhoc Limit, Limit at start of day) not currently surfaced here.
///
/// See DECISIONS.md — "Live mode architecture direction" for why this is
/// isolated from PaperOrderSimulator entirely.
/// </summary>
public class FyersOrderService : IFyersOrderService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly FyersTokenService _tokenService;
    private readonly ILogger<FyersOrderService> _logger;

    public FyersOrderService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        FyersTokenService tokenService,
        ILogger<FyersOrderService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _tokenService = tokenService;
        _logger = logger;
    }

    /// <summary>
    /// Fetches current account funds from Fyers. Read-only — safe to call
    /// as often as needed, including as a pre-entry gate in LiveOrderExecutor.
    /// </summary>
    public async Task<FyersFundsSnapshot> GetFundsAsync(CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient("Fyers");
        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization",
            $"{_configuration["Fyers:ClientId"]}:{_tokenService.AccessToken}");

        var absoluteUrl = "https://api-t1.fyers.in/api/v3/funds";

        _logger.LogInformation("[FyersOrder] Calling: {Url}", absoluteUrl);
        var response = await client.GetAsync(absoluteUrl, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        _logger.LogInformation("[FyersOrder] Response {Status}: {Body}", response.StatusCode, body);

        EnsureSuccess(response, body, "GetFunds");

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        EnsureOk(root, "GetFunds");

        var fundLimits = root.GetProperty("fund_limit").EnumerateArray().ToList();

        var available = FindFundLine(fundLimits, "Available Balance");
        var utilized = FindFundLine(fundLimits, "Utilized Amount");
        var total = FindFundLine(fundLimits, "Total Balance");

        _logger.LogInformation(
            "[FyersOrder] Funds | Available=₹{Available:F0} Utilized=₹{Utilized:F0} Total=₹{Total:F0}",
            available, utilized, total);

        return new FyersFundsSnapshot(
            AvailableMargin: available,
            UtilizedMargin: utilized,
            TotalBalance: total,
            FetchedAt: DateTime.UtcNow);
    }

    private static decimal FindFundLine(List<JsonElement> fundLimits, string title)
    {
        foreach (var line in fundLimits)
        {
            if (line.TryGetProperty("title", out var t) &&
                t.GetString()?.Equals(title, StringComparison.OrdinalIgnoreCase) == true &&
                line.TryGetProperty("equityAmount", out var amt))
            {
                return amt.GetDecimal();
            }
        }
        return 0m;
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
}