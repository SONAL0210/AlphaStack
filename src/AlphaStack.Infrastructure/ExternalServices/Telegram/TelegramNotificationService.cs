using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using AlphaStack.Application.Common.Interfaces;
using Telegram.Bot.Exceptions;

namespace AlphaStack.Infrastructure.ExternalServices.Telegram;

public class TelegramNotificationService : ITelegramNotificationService
{
    private readonly ILogger<TelegramNotificationService> _logger;

    public TelegramNotificationService(ILogger<TelegramNotificationService> logger)
        => _logger = logger;

    public async Task AnswerCallbackQueryAsync(string botToken, string callbackQueryId, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var bot = new TelegramBotClient(botToken);
            await bot.AnswerCallbackQuery(callbackQueryId, cancellationToken: cts.Token);
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException or RequestException)
        {
            _logger.LogWarning("[Telegram] AnswerCallbackQuery timed out — ignored");
        }
    }

    public async Task SendMessageAsync(
        string botToken, long chatId, string message, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var bot = new TelegramBotClient(botToken);
            await bot.SendMessage(chatId, message, parseMode: ParseMode.Markdown, cancellationToken: cts.Token);
            _logger.LogDebug("Sent Telegram message to chat {ChatId}", chatId);
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException or RequestException)
        {
            _logger.LogWarning("[Telegram] SendMessage to chat {ChatId} failed — Telegram unreachable", chatId);
        }
    }

    public async Task<string> SendApprovalRequestAsync(
        string botToken, long chatId, string message, string signalGroupId, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        try
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
                cancellationToken: cts.Token);

            _logger.LogInformation(
                "Sent approval request to chat {ChatId}, messageId: {MessageId}, signalGroup: {GroupId}",
                chatId, sent.MessageId, signalGroupId);

            return sent.MessageId.ToString();
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException or RequestException)
        {
            _logger.LogWarning(
                "[Telegram] SendApprovalRequest failed for GroupId={GroupId} — Telegram unreachable. Use web fallback: /approve",
                signalGroupId);
            return string.Empty; // caller handles empty messageId
        }
    }

    public async Task EditMessageAsync(
        string botToken, long chatId, string messageId, string newText, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(messageId)) return; // no message was sent — skip edit

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var bot = new TelegramBotClient(botToken);
            await bot.EditMessageText(
                chatId, int.Parse(messageId), newText,
                parseMode: ParseMode.Markdown,
                cancellationToken: cts.Token);
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException or RequestException)
        {
            _logger.LogWarning("[Telegram] EditMessage failed — Telegram unreachable");
        }
    }
}
