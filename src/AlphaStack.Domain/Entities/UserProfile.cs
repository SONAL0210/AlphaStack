using AlphaStack.Domain.Common;

namespace AlphaStack.Domain.Entities;

/// <summary>
/// Represents a platform tenant. Each user has their own broker account,
/// Telegram bot, risk parameters, and capital allocation.
///
/// Multi-user model:
/// - Paper mode: Fyers token shared from config (FyersTokenService singleton)
/// - Live mode: per-user Fyers credentials stored here, token refreshed daily
/// </summary>
public class UserProfile : BaseEntity
{
    public string Username { get; private set; } = default!;
    public string Email    { get; private set; } = default!;

    // ── Zerodha Kite Connect (retained for failover) ──────────────────────────
    public string? EncryptedKiteApiKey     { get; private set; }
    public string? EncryptedKiteApiSecret  { get; private set; }
    public string? KiteAccessToken         { get; private set; }
    public DateTime? KiteAccessTokenExpiry { get; private set; }

    // ── Fyers (primary broker) ────────────────────────────────────────────────
    /// <summary>Fyers app client ID e.g. "ABC123-100". Null until live trading setup.</summary>
    public string? FyersClientId            { get; private set; }

    /// <summary>Fyers secret key encrypted at rest. Null until live trading setup.</summary>
    public string? EncryptedFyersSecret     { get; private set; }

    /// <summary>
    /// Current Fyers JWT access token. Refreshed daily via OAuth callback.
    /// Null in paper mode — shared token from config is used instead.
    /// </summary>
    public string? FyersAccessToken         { get; private set; }

    /// <summary>UTC timestamp when FyersAccessToken was last set. Used to detect stale tokens.</summary>
    public DateTime? FyersTokenSetAt        { get; private set; }

    // ── Telegram ──────────────────────────────────────────────────────────────
    public string EncryptedTelegramBotToken { get; private set; } = default!;
    public long   TelegramChatId            { get; private set; }

    // ── Risk parameters ───────────────────────────────────────────────────────
    public decimal TotalCapitalAllocated      { get; private set; }
    public decimal MaxDrawdownPercent         { get; private set; }
    public decimal MaxCapitalPerTradePercent  { get; private set; }

    public bool IsActive { get; private set; } = true;

    // ── Computed ──────────────────────────────────────────────────────────────
    public bool HasZerodhaCredentials =>
        !string.IsNullOrEmpty(EncryptedKiteApiKey) &&
        !string.IsNullOrEmpty(EncryptedKiteApiSecret);

    public bool HasFyersCredentials =>
        !string.IsNullOrEmpty(FyersClientId) &&
        !string.IsNullOrEmpty(EncryptedFyersSecret);

    /// <summary>
    /// True if this user has a fresh Fyers token set today (IST).
    /// Used by FyersTokenReminderService to decide whether to send a reminder.
    /// </summary>
    public bool IsFyersTokenFreshToday()
    {
        if (FyersTokenSetAt is null) return false;
        var ist    = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        var today  = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ist));
        var setDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(FyersTokenSetAt.Value, ist));
        return today == setDay;
    }

    // ── Navigation ────────────────────────────────────────────────────────────
    public IReadOnlyCollection<StrategyExecution> StrategyExecutions =>
        _strategyExecutions.AsReadOnly();
    private readonly List<StrategyExecution> _strategyExecutions = [];

    private UserProfile() { }

    // ── Factory ───────────────────────────────────────────────────────────────

    public static UserProfile Create(
        string  username,
        string  email,
        string? encryptedKiteApiKey,
        string? encryptedKiteApiSecret,
        string  encryptedTelegramBotToken,
        long    telegramChatId,
        decimal totalCapitalAllocated,
        decimal maxDrawdownPercent,
        decimal maxCapitalPerTradePercent)
    {
        return new UserProfile
        {
            Username                  = username,
            Email                     = email,
            EncryptedKiteApiKey       = encryptedKiteApiKey,
            EncryptedKiteApiSecret    = encryptedKiteApiSecret,
            EncryptedTelegramBotToken = encryptedTelegramBotToken,
            TelegramChatId            = telegramChatId,
            TotalCapitalAllocated     = totalCapitalAllocated,
            MaxDrawdownPercent        = maxDrawdownPercent,
            MaxCapitalPerTradePercent = maxCapitalPerTradePercent
        };
    }

    // ── Kite token management ─────────────────────────────────────────────────

    public void SetKiteAccessToken(string accessToken, DateTime expiry)
    {
        KiteAccessToken       = accessToken;
        KiteAccessTokenExpiry = expiry;
        MarkUpdated();
    }

    public void ClearKiteAccessToken()
    {
        KiteAccessToken       = null;
        KiteAccessTokenExpiry = null;
        MarkUpdated();
    }

    public bool IsKiteSessionValid() =>
        KiteAccessToken is not null && KiteAccessTokenExpiry > DateTime.UtcNow;

    // ── Fyers token management ────────────────────────────────────────────────

    /// <summary>
    /// Stores a refreshed Fyers access token for this user.
    /// Called by FyersAuthController after successful OAuth callback.
    /// Live mode only — paper mode uses shared config token.
    /// </summary>
    public void SetFyersAccessToken(string accessToken)
    {
        FyersAccessToken = accessToken;
        FyersTokenSetAt  = DateTime.UtcNow;
        MarkUpdated();
    }

    public void ClearFyersAccessToken()
    {
        FyersAccessToken = null;
        FyersTokenSetAt  = null;
        MarkUpdated();
    }

    /// <summary>Sets Fyers credentials for live trading setup.</summary>
    public void SetFyersCredentials(string clientId, string encryptedSecret)
    {
        FyersClientId         = clientId;
        EncryptedFyersSecret  = encryptedSecret;
        MarkUpdated();
    }

    // ── User management ───────────────────────────────────────────────────────

    public void Deactivate()
    {
        IsActive = false;
        MarkUpdated();
    }
}
