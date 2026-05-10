using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AlphaStack.Infrastructure.ExternalServices.Fyers;

/// <summary>
/// Singleton that owns the current Fyers access token.
/// Token is loaded from config at startup, then updated in-memory
/// when the daily OAuth callback completes.
///
/// FyersMarketDataProvider and InstrumentSyncService read token from here
/// instead of directly from IConfiguration — so a refreshed token is
/// picked up immediately without restarting the app.
/// </summary>
public class FyersTokenService
{
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FyersTokenService> _logger;

    private string _accessToken;
    private DateTime _tokenSetAt;

    public FyersTokenService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<FyersTokenService> logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        // Load token from config at startup
        _accessToken = configuration["Fyers:AccessToken"] ?? string.Empty;
        _tokenSetAt = DateTime.UtcNow.AddDays(-1);

        _logger.LogInformation("[FyersToken] Loaded token from config. Length={L}", _accessToken.Length);
    }

    /// <summary>Current access token — used by HTTP clients.</summary>
    public string AccessToken => _accessToken;

    /// <summary>When the token was last refreshed.</summary>
    public DateTime TokenSetAt => _tokenSetAt;

    /// <summary>
    /// True if token was set today (IST). Used to decide whether
    /// to send a refresh reminder via Telegram.
    /// </summary>
    public bool IsTokenFreshToday()
    {
        var ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ist));
        var setDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(_tokenSetAt, ist));
        return today == setDay;
    }

    /// <summary>
    /// Exchanges an auth_code for an access token via Fyers API.
    /// Called by FyersAuthController when OAuth callback arrives.
    /// Updates in-memory token immediately — no app restart needed.
    /// </summary>
    public async Task<(bool Success, string? Token, string? Error)> ExchangeAuthCodeAsync(
        string authCode,
        CancellationToken ct = default)
    {
        var clientId = _configuration["Fyers:ClientId"]
            ?? throw new InvalidOperationException("Fyers:ClientId not configured");
        var secretKey = _configuration["Fyers:SecretKey"]
            ?? throw new InvalidOperationException("Fyers:SecretKey not configured");

        // Fyers token endpoint
        var payload = new
        {
            grant_type = "authorization_code",
            appIdHash  = ComputeAppIdHash(clientId, secretKey),
            auth_code  = authCode
        };
        var payloadJson = JsonSerializer.Serialize(payload);

        var client = _httpClientFactory.CreateClient("FyersAuth");

        var content = new StringContent(
            payloadJson,    
            System.Text.Encoding.UTF8,
            "application/json");

        _logger.LogInformation("[FyersToken] Exchanging auth_code for access token...");

        var response = await client.PostAsync(
            "https://api-t1.fyers.in/api/v3/validate-authcode", content, ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        _logger.LogInformation("[FyersToken] Token exchange response: {Body}", body);

        // ADD THIS:
        if (!body.TrimStart().StartsWith('{'))
        {
            _logger.LogError("[FyersToken] Non-JSON response from Fyers: {Body}", body);
            return (false, null, $"Unexpected response: {body}");
        }

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        if (!root.TryGetProperty("access_token", out var tokenEl))
        {
            var errMsg = root.TryGetProperty("message", out var msg)
                ? msg.GetString() : body;
            _logger.LogError("[FyersToken] Token exchange failed: {Error}", errMsg);
            return (false, null, errMsg);
        }

        var newToken = tokenEl.GetString()!;
        UpdateToken(newToken);

        return (true, newToken, null);
    }

    /// <summary>
    /// Directly sets a token (e.g. from environment variable update or manual paste).
    /// </summary>
    public void UpdateToken(string newToken)
    {
        _accessToken = newToken;
        _tokenSetAt = DateTime.UtcNow;
        _logger.LogInformation(
            "[FyersToken] Token updated in memory. Length={L} SetAt={T:HH:mm} UTC",
            newToken.Length, _tokenSetAt);
    }

    /// <summary>
    /// Builds the Fyers login URL that the user must visit to authorise.
    /// </summary>
    public string BuildLoginUrl()
    {
        var clientId = _configuration["Fyers:ClientId"]!;
        var redirectUri = _configuration["Fyers:RedirectUri"]!;
        var state = Guid.NewGuid().ToString("N")[..8]; // short random state

        return $"https://api-t1.fyers.in/api/v3/generate-authcode" +
               $"?client_id={Uri.EscapeDataString(clientId)}" +
               $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
               $"&response_type=code" +
               $"&state={state}";
    }

    // SHA-256(clientId:secretKey:authCode)
    private static string ComputeAppIdHash(string clientId, string secretKey)
    {
        // Fyers expects hash of appId WITHOUT the -100 suffix
        var appId = clientId.Contains('-')
            ? clientId[..clientId.LastIndexOf('-')]
            : clientId;

        var input = $"{appId}:{secretKey}";
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
    public void MarkStale()
    {
        _tokenSetAt = DateTime.UtcNow.AddDays(-1);
        _logger.LogInformation("[FyersToken] Token marked stale — will prompt refresh at 8 AM.");
    }
}
