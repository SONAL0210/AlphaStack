using Microsoft.AspNetCore.Mvc;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Infrastructure.ExternalServices.Fyers;

namespace AlphaStack.API.Controllers;

/// <summary>
/// Handles Fyers OAuth callback.
///
/// Flow:
///   1. FyersTokenReminderService sends login URL to Telegram at 8 AM
///   2. User taps URL, logs in to Fyers
///   3. Fyers redirects to GET /api/fyers/callback?auth_code=xxx
///   4. This controller exchanges auth_code for access_token automatically
///   5. Token is stored in FyersTokenService (singleton, in-memory)
///   6. Telegram confirmation sent to user
///   7. App continues with fresh token — no restart needed
/// </summary>
[ApiController]
[Route("api/fyers")]
public class FyersAuthController : ControllerBase
{
    private readonly FyersTokenService _tokenService;
    private readonly IUserProfileRepository _userRepo;
    private readonly ITelegramNotificationService _telegram;
    private readonly IEncryptionService _encryption;
    private readonly ILogger<FyersAuthController> _logger;

    public FyersAuthController(
        FyersTokenService tokenService,
        IUserProfileRepository userRepo,
        ITelegramNotificationService telegram,
        IEncryptionService encryption,
        ILogger<FyersAuthController> logger)
    {
        _tokenService = tokenService;
        _userRepo     = userRepo;
        _telegram     = telegram;
        _encryption   = encryption;
        _logger       = logger;
    }

    /// <summary>
    /// Fyers OAuth callback — auto-exchanges auth_code for access token.
    /// Returns a simple HTML page so the user knows it worked.
    /// </summary>
    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string auth_code,
        [FromQuery] string? state,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "[FyersAuth] Callback received. auth_code length={Length} state={State}",
            auth_code?.Length ?? 0,
            state);

        if (string.IsNullOrWhiteSpace(auth_code))
            return Content(HtmlPage("❌ Error", "No auth_code received. Please try logging in again."), "text/html");

        var (success, token, error) = await _tokenService.ExchangeAuthCodeAsync(auth_code, ct);

        if (!success)
        {
            _logger.LogError("[FyersAuth] Token exchange failed: {Error}", error);
            return Content(
                HtmlPage("❌ Token Exchange Failed", $"Error: {error}<br/>Please try again."),
                "text/html");
        }

        // Notify all active users via Telegram
        _ = Task.Run(async () =>
        {
            try
            {
                var users = await _userRepo.GetAllActiveAsync(CancellationToken.None);
                foreach (var user in users)
                {
                    var botToken = _encryption.Decrypt(user.EncryptedTelegramBotToken);
                    await _telegram.SendMessageAsync(
                        botToken, user.TelegramChatId,
                        $"✅ *Fyers Token Refreshed*\n" +
                        $"Token updated at {DateTime.Now:HH:mm} IST\\.\n" +
                        $"Ready for 9:20 AM entry evaluation\\.",
                        CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[FyersAuth] Telegram notification after token refresh failed.");
            }
        }, CancellationToken.None);

        _logger.LogInformation("[FyersAuth] Token refreshed successfully.");

        return Content(
            HtmlPage("✅ Token Refreshed",
                "Your Fyers token has been updated successfully.<br/>" +
                "You can close this tab and return to Telegram."),
            "text/html");
    }
    [HttpGet("login-url")]
    public IActionResult GetLoginUrl()
    {
        var url = _tokenService.BuildLoginUrl();
        return Ok(new { loginUrl = url });
    }

    /// <summary>
    /// Manual token update endpoint — paste token directly if OAuth flow isn't available.
    /// POST /api/fyers/token  { "token": "eyJ..." }
    /// </summary>
    [HttpPost("token")]
    public IActionResult UpdateTokenManually([FromBody] ManualTokenRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
            return BadRequest("Token is required.");

        _tokenService.UpdateToken(request.Token);

        _logger.LogInformation("[FyersAuth] Token manually updated via API.");
        return Ok(new { message = "Token updated successfully.", setAt = _tokenService.TokenSetAt });
    }

    /// <summary>Returns current token status (not the token itself).</summary>
    [HttpGet("token-status")]
    public IActionResult TokenStatus()
    {
        return Ok(new
        {
            isFreshToday = _tokenService.IsTokenFreshToday(),
            tokenSetAt   = _tokenService.TokenSetAt,
            tokenLength  = _tokenService.AccessToken.Length
        });
    }

    private static string HtmlPage(string title, string body)
    {
        return "<!DOCTYPE html><html><head>" +
            $"<title>{title}</title>" +
            "<meta name='viewport' content='width=device-width, initial-scale=1'>" +
            "<style>" +
            "body { font-family: -apple-system, sans-serif; display: flex; justify-content: center;" +
            "align-items: center; height: 100vh; margin: 0; background: #f5f5f5; }" +
            ".card { background: white; padding: 2rem; border-radius: 12px;" +
            "box-shadow: 0 2px 12px rgba(0,0,0,0.1); text-align: center; max-width: 400px; }" +
            "h1 { font-size: 1.5rem; margin-bottom: 1rem; }" +
            "p { color: #666; line-height: 1.6; }" +
            "</style></head><body>" +
            "<div class='card'>" +
            $"<h1>{title}</h1>" +
            $"<p>{body}</p>" +
            "</div></body></html>";
    }

    public record ManualTokenRequest(string Token);
}
