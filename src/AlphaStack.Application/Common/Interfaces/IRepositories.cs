using AlphaStack.Domain.Entities;

namespace AlphaStack.Application.Common.Interfaces;

public interface IUserProfileRepository
{
    Task<UserProfile?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<UserProfile?> GetByUsernameAsync(string username, CancellationToken ct = default);
    Task<IReadOnlyList<UserProfile>> GetAllActiveAsync(CancellationToken ct = default);
    Task AddAsync(UserProfile userProfile, CancellationToken ct = default);
    Task UpdateAsync(UserProfile userProfile, CancellationToken ct = default);
}

public interface IStrategyDefinitionRepository
{
    Task<StrategyDefinition?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<StrategyDefinition?> GetByTypeAsync(string strategyType, CancellationToken ct = default);
    Task<IReadOnlyList<StrategyDefinition>> GetAllActiveAsync(CancellationToken ct = default);
    Task AddAsync(StrategyDefinition definition, CancellationToken ct = default);
    Task UpdateAsync(StrategyDefinition definition, CancellationToken ct = default);
}

public interface IStrategyExecutionRepository
{
    Task<StrategyExecution?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<StrategyExecution>> GetRunningExecutionsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<StrategyExecution>> GetByUserAsync(Guid userProfileId, CancellationToken ct = default);
    Task AddAsync(StrategyExecution execution, CancellationToken ct = default);
    Task UpdateAsync(StrategyExecution execution, CancellationToken ct = default);
}

public interface ITradeOrderRepository
{
    Task<TradeOrder?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<TradeOrder>> GetBySignalGroupAsync(Guid signalGroupId, CancellationToken ct = default);
    Task<IReadOnlyList<TradeOrder>> GetPendingApprovalAsync(CancellationToken ct = default);
    Task AddAsync(TradeOrder order, CancellationToken ct = default);
    Task UpdateAsync(TradeOrder order, CancellationToken ct = default);
    Task<List<TradeOrder>> GetAllAsync(CancellationToken ct = default);
}

public interface ITradeRepository
{
    Task<Trade?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Trade?> GetByEntrySignalGroupAsync(Guid signalGroupId, CancellationToken ct = default);
    Task<IReadOnlyList<Trade>> GetOpenByExecutionAsync(Guid strategyExecutionId, CancellationToken ct = default);
    Task<IReadOnlyList<Trade>> GetByExecutionAsync(Guid strategyExecutionId, CancellationToken ct = default);
    Task AddAsync(Trade trade, CancellationToken ct = default);
    Task UpdateAsync(Trade trade, CancellationToken ct = default);
}

public interface IPositionRepository
{
    Task<Position?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Position>> GetOpenByExecutionAsync(Guid strategyExecutionId, CancellationToken ct = default);
    Task<IReadOnlyList<Position>> GetBySignalGroupAsync(Guid signalGroupId, CancellationToken ct = default);
    Task AddAsync(Position position, CancellationToken ct = default);
    Task UpdateAsync(Position position, CancellationToken ct = default);
}

public interface IInstrumentRepository
{
    Task<Domain.Entities.Instrument?> GetByTokenAsync(int instrumentToken, CancellationToken ct = default);
    Task<Domain.Entities.Instrument?> GetBySymbolAndExchangeAsync(string symbol, string exchange, CancellationToken ct = default);
    Task<IReadOnlyList<Domain.Entities.Instrument>> FindOptionsAsync(
        string underlyingSymbol,
        DateOnly expiry,
        Domain.Enums.OptionType optionType,
        CancellationToken ct = default);
    Task BulkUpsertAsync(IEnumerable<Domain.Entities.Instrument> instruments, CancellationToken ct = default);
}

// ── NEW: Trade Analytics ───────────────────────────────────────────────────────

public interface ITradeAnalyticsRepository
{
    /// <summary>Get analytics by the TradeId (which we set to SignalGroupId).</summary>
    Task<TradeAnalytics?> GetByTradeIdAsync(Guid tradeId, CancellationToken ct = default);

    /// <summary>All closed analytics records — used for CSV export and analysis.</summary>
    Task<List<TradeAnalytics>> GetAllClosedAsync(CancellationToken ct = default);

    /// <summary>All analytics records regardless of status.</summary>
    Task<List<TradeAnalytics>> GetAllAsync(CancellationToken ct = default);

    Task AddAsync(TradeAnalytics analytics, CancellationToken ct = default);
    Task UpdateAsync(TradeAnalytics analytics, CancellationToken ct = default);
}

public interface IShadowTradeRepository
{
    /// <summary>Bulk insert all variants for a single signal evaluation.</summary>
    Task AddRangeAsync(IEnumerable<ShadowTrade> trades, CancellationToken ct = default);

    /// <summary>All shadow trades still open — used by exit simulator job.</summary>
    Task<List<ShadowTrade>> GetOpenAsync(CancellationToken ct = default);

    Task UpdateAsync(ShadowTrade trade, CancellationToken ct = default);

    /// <summary>All shadow trades — used for CSV export.</summary>
    Task<List<ShadowTrade>> GetAllAsync(CancellationToken ct = default);
}
