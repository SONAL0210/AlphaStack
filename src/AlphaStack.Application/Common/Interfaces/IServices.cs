using AlphaStack.Domain.Entities;
using AlphaStack.Domain.Enums;

namespace AlphaStack.Application.Common.Interfaces;

// ─── Kite Connect ────────────────────────────────────────────────────────────

public interface IKiteAuthService
{
    string GetLoginUrl(string apiKey , string userId);
    Task<KiteSessionResult> GenerateSessionAsync(string apiKey, string apiSecret, string requestToken, CancellationToken ct = default);
}

public record KiteSessionResult(string AccessToken, string PublicToken, string UserId, DateTime ExpiresAt);

public interface IKiteMarketDataService
{
    Task<Quote> GetQuoteAsync(string userProfileId, string tradingSymbol, string exchange, CancellationToken ct = default);
    Task<IReadOnlyList<Quote>> GetQuotesAsync(string userProfileId, IEnumerable<string> symbols, CancellationToken ct = default);
    Task<IReadOnlyList<Candle>> GetHistoricalDataAsync(string userProfileId, int instrumentToken, string interval, DateTime from, DateTime to, CancellationToken ct = default);
    Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(string exchange, CancellationToken ct = default);
}

public interface IMarketDataProvider
{
    Task<Quote?> GetQuoteAsync(string symbol, string exchange, CancellationToken ct = default);
    Task<List<Candle>> GetHistoricalDataAsync(
        int instrumentToken,
        string interval,
        DateTime from,
        DateTime to,
        CancellationToken ct = default);
}

public record Quote(
    string TradingSymbol,
    string Exchange,
    decimal LastPrice,
    decimal BidPrice,
    decimal AskPrice,
    decimal OpenPrice,
    decimal HighPrice,
    decimal LowPrice,
    decimal ClosePrice,
    long Volume,
    decimal OpenInterest,
    DateTime Timestamp);

public record Candle(
    DateTime Timestamp,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume);

// ─── Telegram ────────────────────────────────────────────────────────────────

public interface ITelegramNotificationService
{
    Task SendMessageAsync(string botToken, long chatId, string message, CancellationToken ct = default);
    Task<string> SendApprovalRequestAsync(string botToken, long chatId, string message, string signalGroupId, CancellationToken ct = default);
    Task EditMessageAsync(string botToken, long chatId, string messageId, string newText, CancellationToken ct = default);
    Task AnswerCallbackQueryAsync(string botToken, string callbackQueryId, CancellationToken ct = default);
}

// ─── Market Data ─────────────────────────────────────────────────────────────

public interface IMarketDataService
{
    Task<Quote?> GetCachedQuoteAsync(string tradingSymbol, CancellationToken ct = default);
    Task RefreshQuotesAsync(IEnumerable<string> symbols, CancellationToken ct = default);
}

// ─── Encryption ──────────────────────────────────────────────────────────────

public interface IEncryptionService
{
    string Encrypt(string plaintext);
    string Decrypt(string ciphertext);
}

// ─── Unit of Work ─────────────────────────────────────────────────────────────

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

// ─── Risk Manager ─────────────────────────────────────────────────────────────

/// <summary>
/// Structured result from a risk check.
/// Defined here (Interfaces layer) so RiskManager and SignalProcessor share
/// the same type without a circular dependency.
/// </summary>
public record RiskValidationResult(bool IsAllowed, string? Reason)
{
    public static RiskValidationResult Allow() => new(true, null);
    public static RiskValidationResult Reject(string reason) => new(false, reason);
}

public interface IRiskManager
{
    Task<RiskValidationResult> ValidateEntryAsync(
        StrategyExecution execution,
        UserProfile user,
        decimal estimatedTradeCapital,
        CancellationToken ct = default);
}
