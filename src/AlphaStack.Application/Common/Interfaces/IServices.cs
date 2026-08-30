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

public interface IFyersOrderService
{
    Task<FyersFundsSnapshot> GetFundsAsync(CancellationToken ct = default);
    Task<FyersOrderResult> PlaceOrderAsync(FyersOrderRequest request, CancellationToken ct = default);
    Task<FyersOrderResult> CancelOrderAsync(string brokerOrderId, CancellationToken ct = default);
    Task<FyersOrderStatus?> GetOrderStatusAsync(string brokerOrderId, CancellationToken ct = default);
    Task<FyersOrderResult> PlaceMultilegOrderAsync(FyersMultilegOrderRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<FyersOrderStatus>> GetOrdersByTagAsync(string orderTag, CancellationToken ct = default);
}

/// <summary>
/// Request for POST /api/v3/multileg/orders/sync — 2 or 3 legs, IOC only,
/// all-or-nothing atomicity confirmed via Fyers support (see DECISIONS.md).
/// NOT YET LIVE-TESTED — schema confirmed from official docs only. Do not
/// treat this as equally trustworthy to PlaceOrderAsync until a real
/// place+cancel round-trip has been fired, same discipline used there.
/// </summary>
public record FyersMultilegOrderRequest(
    string OrderTag,
    string ProductType,   // INTRADAY | MARGIN — NFO segments only
    string OrderType,     // "2L" or "3L"
    IReadOnlyList<FyersMultilegLeg> Legs);

public record FyersMultilegLeg(
    string Symbol,
    int Quantity,
    int Side,
    decimal LimitPrice);

/// <summary>
/// One order's current state from GET /api/v3/orders (orderBook array).
/// status: 1=Cancelled, 2=Traded/Filled, 4=Transit, 5=Rejected, 6=Pending, 7=Expired
/// (confirmed via Fyers docs — 3 explicitly "not used currently").
/// </summary>
public record FyersOrderStatus(
    string BrokerOrderId,
    int Status,
    int FilledQty,
    int RemainingQty,
    decimal TradedPrice,
    string Message);

/// <summary>
/// Request shape for POST /api/v3/orders/sync — verified against a real
/// placed-and-cancelled order (Aug 2026), not documentation alone.
/// type: 1=Limit, 2=Market, 3=Stop(SL-M), 4=Stoplimit(SL-L)
/// side: 1=Buy, -1=Sell
/// orderTag: alphanumeric only — hyphens rejected by Fyers, confirmed live.
///           Use Guid.ToString("N") (no hyphens), matching the existing
///           convention in PaperOrderSimulator's ClientOrderId fallback.
/// </summary>
public record FyersOrderRequest(
    string Symbol,
    int Quantity,
    int Type,
    int Side,
    string ProductType,
    decimal LimitPrice,
    decimal StopPrice,
    string Validity,
    string OrderTag);

public record FyersOrderResult(
    bool Success,
    string? BrokerOrderId,
    int Code,
    string Message);

/// <summary>
/// Snapshot of account funds from Fyers' funds endpoint. AvailableMargin is
/// what LiveOrderExecutor's pre-entry funds-check compares estimatedTradeCapital
/// against — the same number RiskManager already computes at signal time,
/// checked again here as a live, broker-side confirmation immediately before
/// an order is placed (RiskManager's check can be seconds to minutes stale
/// by the time approval completes; this one is not).
/// </summary>
public record FyersFundsSnapshot(
    decimal AvailableMargin,
    decimal UtilizedMargin,
    decimal TotalBalance,
    DateTime FetchedAt);

// ─── Liquidity Guard ───────────────────────────────────────────────────────

public interface ILiquidityGuard
{
    Task<RiskValidationResult> ValidateSignalLiquidityAsync(
        StrategySignal signal,
        CancellationToken ct = default);
}