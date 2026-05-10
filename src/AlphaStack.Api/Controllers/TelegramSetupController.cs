using Microsoft.AspNetCore.Mvc;
using AlphaStack.Application.Common.Interfaces;

namespace AlphaStack.API.Controllers;

/// <summary>
/// One-time setup operations for each user's Telegram bot.
/// Run these once after onboarding a new user.
/// </summary>
[ApiController]
[Route("api/telegram")]
public class TelegramSetupController : ControllerBase
{
    private readonly IUserProfileRepository _userRepo;
    private readonly IEncryptionService _encryption;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TelegramSetupController> _logger;

    public TelegramSetupController(
        IUserProfileRepository userRepo,
        IEncryptionService encryption,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<TelegramSetupController> logger)
    {
        _userRepo = userRepo;
        _encryption = encryption;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Register the webhook URL with Telegram for a user's bot.
    /// Must be called once per user after onboarding.
    /// POST /api/telegram/setup-webhook?userId={guid}
    ///
    /// Telegram will POST updates to:
    ///   {BaseUrl}/api/telegram/webhook/{userId}
    /// </summary>
    [HttpPost("setup-webhook")]
    public async Task<IActionResult> SetupWebhook([FromQuery] Guid userId, CancellationToken ct)
    {
        var user = await _userRepo.GetByIdAsync(userId, ct);
        if (user is null) return NotFound();

        var botToken = _encryption.Decrypt(user.EncryptedTelegramBotToken);
        var baseUrl  = _configuration["KiteConnect:RedirectUrl"]
            ?.Replace("/api/kite/callback", string.Empty)  // strip Kite path to get base
            ?? throw new InvalidOperationException("Base URL not configured.");

        var webhookUrl = $"{baseUrl}/api/telegram/webhook/{userId}";

        var client   = _httpClientFactory.CreateClient();
        var response = await client.PostAsync(
            $"https://api.telegram.org/bot{botToken}/setWebhook?url={Uri.EscapeDataString(webhookUrl)}",
            null, ct);

        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to register Telegram webhook: {Body}", body);
            return BadRequest(new { error = "Telegram webhook registration failed.", detail = body });
        }

        _logger.LogInformation(
            "Telegram webhook registered for user {UserId}: {Url}", userId, webhookUrl);

        return Ok(new { message = "Webhook registered.", webhookUrl });
    }

    /// <summary>
    /// Check the currently registered webhook info for a user's bot.
    /// GET /api/telegram/webhook-info?userId={guid}
    /// </summary>
    [HttpGet("webhook-info")]
    public async Task<IActionResult> GetWebhookInfo([FromQuery] Guid userId, CancellationToken ct)
    {
        var user = await _userRepo.GetByIdAsync(userId, ct);
        if (user is null) return NotFound();

        var botToken = _encryption.Decrypt(user.EncryptedTelegramBotToken);
        var client   = _httpClientFactory.CreateClient();
        var response = await client.GetAsync(
            $"https://api.telegram.org/bot{botToken}/getWebhookInfo", ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        return Content(body, "application/json");
    }
}
