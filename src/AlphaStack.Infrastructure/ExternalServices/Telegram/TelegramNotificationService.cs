using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using AlphaStack.Application.Common.Interfaces;

namespace AlphaStack.Infrastructure.ExternalServices.Telegram;

public class TelegramNotificationService : ITelegramNotificationService
{
    private readonly ILogger<TelegramNotificationService> _logger;

    public TelegramNotificationService(ILogger<TelegramNotificationService> logger)
        => _logger = logger;

    public async Task AnswerCallbackQueryAsync(string botToken, string callbackQueryId, CancellationToken ct = default)
    {
        var bot = new TelegramBotClient(botToken);
        await bot.AnswerCallbackQuery(callbackQueryId, cancellationToken: ct);
    }
    public async Task SendMessageAsync(
        string botToken, long chatId, string message, CancellationToken ct = default)
    {
        var bot = new TelegramBotClient(botToken);
        await bot.SendMessage(chatId, message, parseMode: ParseMode.Markdown, cancellationToken: ct);
        _logger.LogDebug("Sent Telegram message to chat {ChatId}", chatId);
    }

    /// <summary>
    /// Sends approval request. Callback data format: "{action}:{signalGroupId}"
    /// e.g. "approve:3fa85f64-..." or "reject:3fa85f64-..."
    /// This ensures the webhook can unambiguously resolve which signal group
    /// was acted on, even if multiple signals are pending simultaneously.
    /// </summary>
    public async Task<string> SendApprovalRequestAsync(
        string botToken, long chatId, string message, string signalGroupId, CancellationToken ct = default)
    {
        var bot = new TelegramBotClient(botToken);

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("✅ Approve", $"approve:{signalGroupId}"),
                InlineKeyboardButton.WithCallbackData("❌ Reject",  $"reject:{signalGroupId}")
            }
        });

        var sent = await bot.SendMessage(
            chatId, message,
            parseMode: ParseMode.Markdown,
            replyMarkup: keyboard,
            cancellationToken: ct);

        _logger.LogInformation(
            "Sent approval request to chat {ChatId}, messageId: {MessageId}, signalGroup: {GroupId}",
            chatId, sent.MessageId, signalGroupId);

        return sent.MessageId.ToString();
    }

    public async Task EditMessageAsync(
        string botToken, long chatId, string messageId, string newText, CancellationToken ct = default)
    {
        var bot = new TelegramBotClient(botToken);
        await bot.EditMessageText(
            chatId, int.Parse(messageId), newText,
            parseMode: ParseMode.Markdown,
            cancellationToken: ct);
    }
}
