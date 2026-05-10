using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AlphaStack.Application.Common.Interfaces;

namespace AlphaStack.Infrastructure.BackgroundServices;

/// <summary>
/// Sends a Fyers login link via Telegram every morning at 8:00 AM IST.
/// User taps the link, logs in to Fyers, and the OAuth callback
/// automatically refreshes the token — no manual copy-paste needed.
///
/// Also sends a warning at 9:10 AM IST if token still hasn't been refreshed.
/// </summary>
public class FyersTokenReminderService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Infrastructure.ExternalServices.Fyers.FyersTokenService _tokenService;
    private readonly ILogger<FyersTokenReminderService> _logger;
    private static readonly TimeOnly MidnightReset = new(0, 1); // 12:01 AM IST
    private DateOnly _lastResetDate = DateOnly.MinValue;

    private static readonly TimeZoneInfo Ist =
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    private static readonly TimeOnly ReminderTime = new(8, 00);
    private static readonly TimeOnly WarningTime  = new(9, 10);

    private DateOnly _lastReminderDate = DateOnly.MinValue;
    private DateOnly _lastWarningDate  = DateOnly.MinValue;

    public FyersTokenReminderService(
        IServiceScopeFactory scopeFactory,
        Infrastructure.ExternalServices.Fyers.FyersTokenService tokenService,
        ILogger<FyersTokenReminderService> logger)
    {
        _scopeFactory = scopeFactory;
        _tokenService = tokenService;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[FyersReminder] Service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                
                var istNow   = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Ist);
                var timeNow  = TimeOnly.FromDateTime(istNow);
                var dateToday = DateOnly.FromDateTime(istNow);
                var isWeekday = istNow.DayOfWeek != DayOfWeek.Saturday
                             && istNow.DayOfWeek != DayOfWeek.Sunday;

                if (timeNow >= MidnightReset && _lastResetDate != dateToday)
                {
                    _lastResetDate = dateToday;
                    _tokenService.MarkStale();
                    _logger.LogInformation("[FyersReminder] Token marked stale at midnight.");
                }
                if (isWeekday)
                {
                    // 8:00 AM — send login link
                    if (timeNow >= ReminderTime && _lastReminderDate != dateToday)
                    {
                        _lastReminderDate = dateToday;
                        await SendLoginLinkAsync(stoppingToken);
                    }

                    // 9:10 AM — warn if token still not refreshed
                    if (timeNow >= WarningTime && _lastWarningDate != dateToday
                        && !_tokenService.IsTokenFreshToday())
                    {
                        _lastWarningDate = dateToday;
                        await SendWarningAsync(stoppingToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[FyersReminder] Error in reminder cycle.");
            }

            // Check every minute
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task SendLoginLinkAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var userRepo    = scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();
            var telegram    = scope.ServiceProvider.GetRequiredService<ITelegramNotificationService>();
            var encryption  = scope.ServiceProvider.GetRequiredService<IEncryptionService>();

            var users = await userRepo.GetAllActiveAsync(ct);
            var loginUrl = _tokenService.BuildLoginUrl();

            foreach (var user in users)
            {
                try
                {
                    var botToken = encryption.Decrypt(user.EncryptedTelegramBotToken);

                    var msg =
                        $"🔐 *Daily Token Refresh*\n\n" +
                        $"Tap the link below to login to Fyers\\.\n" +
                        $"Your token will refresh automatically\\.\n\n" +
                        $"[🔑 Login to Fyers]({loginUrl})\n\n" +
                        $"_Token must be refreshed before 9:20 AM for trades to execute\\._";

                    await telegram.SendMessageAsync(botToken, user.TelegramChatId, msg, ct);

                    _logger.LogInformation(
                        "[FyersReminder] Login link sent to user {UserId}", user.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[FyersReminder] Failed to send login link to user {UserId}", user.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FyersReminder] SendLoginLinkAsync failed.");
        }
    }

    private async Task SendWarningAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var userRepo    = scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();
            var telegram    = scope.ServiceProvider.GetRequiredService<ITelegramNotificationService>();
            var encryption  = scope.ServiceProvider.GetRequiredService<IEncryptionService>();

            var users    = await userRepo.GetAllActiveAsync(ct);
            var loginUrl = _tokenService.BuildLoginUrl();

            foreach (var user in users)
            {
                try
                {
                    var botToken = encryption.Decrypt(user.EncryptedTelegramBotToken);

                    var msg =
                        $"⚠️ *Token Not Refreshed*\n\n" +
                        $"Fyers token has not been updated today\\.\n" +
                        $"Trades will fail at 9:20 AM without a fresh token\\.\n\n" +
                        $"[🔑 Login Now]({loginUrl})";

                    await telegram.SendMessageAsync(botToken, user.TelegramChatId, msg, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[FyersReminder] Failed to send warning to user {UserId}", user.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FyersReminder] SendWarningAsync failed.");
        }
    }
}
