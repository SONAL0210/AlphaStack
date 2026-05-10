using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using AlphaStack.Application.Common.Interfaces;

namespace AlphaStack.Infrastructure.ExternalServices.KiteConnect;

/// <summary>
/// Handles Kite Connect OAuth flow:
/// 1. Generate login URL → user visits it → Zerodha redirects back with request_token
/// 2. Exchange request_token for access_token (valid until midnight IST)
/// </summary>
public class KiteAuthService : IKiteAuthService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<KiteAuthService> _logger;

    public KiteAuthService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<KiteAuthService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Step 1: Return the URL the user must open in a browser to log in.
    /// Format: https://kite.zerodha.com/connect/login?v=3&api_key={apiKey}
    /// </summary>
    public string GetLoginUrl(string apiKey, string userId)
    {
        var loginBase = _configuration["KiteConnect:LoginUrl"]
            ?? "https://kite.zerodha.com/connect/login";
        
        // Zerodha echoes back whatever is in redirect_params
        var redirectParams = Uri.EscapeDataString($"userId={userId}");
        return $"{loginBase}?v=3&api_key={apiKey}&redirect_params={redirectParams}";
    }

    /// <summary>
    /// Step 2: Exchange request_token (from OAuth callback) for an access_token.
    /// Kite requires a checksum: SHA-256(api_key + request_token + api_secret)
    /// </summary>
    public async Task<KiteSessionResult> GenerateSessionAsync(
        string apiKey,
        string apiSecret,
        string requestToken,
        CancellationToken ct = default)
    {
        var checksum = ComputeChecksum(apiKey, requestToken, apiSecret);

        var payload = new Dictionary<string, string>
        {
            ["api_key"] = apiKey,
            ["request_token"] = requestToken,
            ["checksum"] = checksum
        };

        var client = _httpClientFactory.CreateClient("KiteConnect");
        client.DefaultRequestHeaders.Remove("Authorization");

        _logger.LogInformation("Exchanging request_token for access_token. ApiKey: {ApiKey}", apiKey);

        var response = await client.PostAsync(
            "/session/token",
            new FormUrlEncodedContent(payload),
            ct);

        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Kite session token exchange failed. Status: {Status}, Body: {Body}",
                response.StatusCode, responseBody);
            throw new KiteAuthException(
                $"Kite session token exchange failed ({response.StatusCode}): {responseBody}");
        }

        var json = JsonDocument.Parse(responseBody);
        var data = json.RootElement.GetProperty("data");

        var accessToken = data.GetProperty("access_token").GetString()
            ?? throw new KiteAuthException("access_token missing from Kite response.");
        var publicToken = data.GetProperty("public_token").GetString() ?? string.Empty;
        var userId = data.GetProperty("user_id").GetString() ?? string.Empty;

        // Access token is valid until midnight IST (UTC+5:30)
        var expiry = GetMidnightIst();

        _logger.LogInformation(
            "Kite session established. UserId: {UserId}, ExpiresAt: {Expiry}",
            userId, expiry);

        return new KiteSessionResult(accessToken, publicToken, userId, expiry);
    }

    /// <summary>
    /// SHA-256(api_key + request_token + api_secret) as hex string.
    /// </summary>
    private static string ComputeChecksum(string apiKey, string requestToken, string apiSecret)
    {
        var raw = apiKey + requestToken + apiSecret;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Kite access tokens expire at midnight IST.
    /// </summary>
    private static DateTime GetMidnightIst()
    {
        var ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        var nowIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ist);
        var midnightIst = nowIst.Date.AddDays(1); // next midnight in IST
        return TimeZoneInfo.ConvertTimeToUtc(midnightIst, ist);
    }
}

public class KiteAuthException : Exception
{
    public KiteAuthException(string message) : base(message) { }
    public KiteAuthException(string message, Exception inner) : base(message, inner) { }
}
