using Microsoft.EntityFrameworkCore;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Domain.Entities;
using AlphaStack.Domain.Enums;

namespace AlphaStack.Infrastructure.Persistence.Repositories;

// ─── UserProfile ─────────────────────────────────────────────────────────────

public class UserProfileRepository : BaseRepository<UserProfile>, IUserProfileRepository
{
    public UserProfileRepository(TradingDbContext db) : base(db) { }

    public new async Task<UserProfile?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await Set.Include(u => u.StrategyExecutions).FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<UserProfile?> GetByUsernameAsync(string username, CancellationToken ct = default)
        => Set.FirstOrDefaultAsync(u => u.Username == username, ct);

    public async Task<IReadOnlyList<UserProfile>> GetAllActiveAsync(CancellationToken ct = default)
        => await Set.Where(u => u.IsActive).ToListAsync(ct);
}

// ─── StrategyDefinition ───────────────────────────────────────────────────────

public class StrategyDefinitionRepository : BaseRepository<StrategyDefinition>, IStrategyDefinitionRepository
{
    public StrategyDefinitionRepository(TradingDbContext db) : base(db) { }

    public Task<StrategyDefinition?> GetByTypeAsync(string strategyType, CancellationToken ct = default)
        => Set.FirstOrDefaultAsync(s => s.StrategyType == strategyType, ct);

    public async Task<IReadOnlyList<StrategyDefinition>> GetAllActiveAsync(CancellationToken ct = default)
        => await Set.Where(s => s.IsActive).ToListAsync(ct);
}

// ─── StrategyExecution ───────────────────────────────────────────────────────

public class StrategyExecutionRepository : BaseRepository<StrategyExecution>, IStrategyExecutionRepository
{
    public StrategyExecutionRepository(TradingDbContext db) : base(db) { }

    public async Task<IReadOnlyList<StrategyExecution>> GetRunningExecutionsAsync(CancellationToken ct = default)
        => await Set
            .Include(e => e.UserProfile)
            .Include(e => e.StrategyDefinition)
            .Where(e => e.IsRunning)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<StrategyExecution>> GetByUserAsync(Guid userProfileId, CancellationToken ct = default)
        => await Set
            .Include(e => e.StrategyDefinition)
            .Where(e => e.UserProfileId == userProfileId)
            .ToListAsync(ct);
}

// ─── TradeOrder ──────────────────────────────────────────────────────────────

public class TradeOrderRepository : BaseRepository<TradeOrder>, ITradeOrderRepository
{
    public TradeOrderRepository(TradingDbContext db) : base(db) { }

    public async Task<IReadOnlyList<TradeOrder>> GetBySignalGroupAsync(Guid signalGroupId, CancellationToken ct = default)
        => await Set.Where(o => o.SignalGroupId == signalGroupId).ToListAsync(ct);

    public async Task<IReadOnlyList<TradeOrder>> GetPendingApprovalAsync(CancellationToken ct = default)
        => await Set.Where(o => o.Status == OrderStatus.Pending).ToListAsync(ct);
    
    public async Task<List<TradeOrder>> GetAllAsync(CancellationToken ct = default)
    {
        return await _db.TradeOrders.ToListAsync(ct);
    }
}

// ─── Trade ───────────────────────────────────────────────────────────────────

public class TradeRepository : BaseRepository<Trade>, ITradeRepository
{
    public TradeRepository(TradingDbContext db) : base(db) { }

    public Task<Trade?> GetByEntrySignalGroupAsync(Guid signalGroupId, CancellationToken ct = default)
        => Set.FirstOrDefaultAsync(t => t.EntrySignalGroupId == signalGroupId, ct);

    public async Task<IReadOnlyList<Trade>> GetOpenByExecutionAsync(Guid strategyExecutionId, CancellationToken ct = default)
        => await Set
            .Where(t => t.StrategyExecutionId == strategyExecutionId
                     && t.Status != TradeStatus.Closed
                     && t.Status != TradeStatus.Failed)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Trade>> GetByExecutionAsync(Guid strategyExecutionId, CancellationToken ct = default)
        => await Set
            .Where(t => t.StrategyExecutionId == strategyExecutionId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);
}

// ─── Position ────────────────────────────────────────────────────────────────

public class PositionRepository : BaseRepository<Position>, IPositionRepository
{
    public PositionRepository(TradingDbContext db) : base(db) { }

    public async Task<IReadOnlyList<Position>> GetOpenByExecutionAsync(Guid strategyExecutionId, CancellationToken ct = default)
        => await Set
            .Where(p => p.StrategyExecutionId == strategyExecutionId && p.Status == PositionStatus.Open)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Position>> GetBySignalGroupAsync(Guid signalGroupId, CancellationToken ct = default)
        => await Set.Where(p => p.SignalGroupId == signalGroupId).ToListAsync(ct);
}

// ─── Instrument ──────────────────────────────────────────────────────────────

public class InstrumentRepository : BaseRepository<Instrument>, IInstrumentRepository
{
    public InstrumentRepository(TradingDbContext db) : base(db) { }

    public Task<Instrument?> GetByTokenAsync(int instrumentToken, CancellationToken ct = default)
        => Set.FirstOrDefaultAsync(i => i.InstrumentToken == instrumentToken, ct);

    public Task<Instrument?> GetBySymbolAndExchangeAsync(string symbol, string exchange, CancellationToken ct = default)
        => Set.FirstOrDefaultAsync(i => i.TradingSymbol == symbol && i.Exchange == exchange, ct);

    public async Task<IReadOnlyList<Instrument>> FindOptionsAsync(
        string underlyingSymbol,
        DateOnly expiry,
        OptionType optionType,
        CancellationToken ct = default)
        => await Set
            .Where(i => i.Name == underlyingSymbol
                     && i.ExpiryDate == expiry
                     && i.OptionType == optionType)
            .OrderBy(i => i.StrikePrice)
            .ToListAsync(ct);

    public async Task BulkUpsertAsync(IEnumerable<Instrument> instruments, CancellationToken ct = default)
    {
        var instrumentList = instruments.ToList();
        if (!instrumentList.Any()) return;

        // Get all existing tokens in one query instead of N queries
        var incomingTokens = instrumentList.Select(i => i.InstrumentToken).ToList();
        var existingTokens = (await Set
            .Where(i => incomingTokens.Contains(i.InstrumentToken))
            .Select(i => i.InstrumentToken)
            .ToListAsync(ct))
            .ToHashSet();

        // Only add instruments that don't exist yet
        var newInstruments = instrumentList
            .Where(i => !existingTokens.Contains(i.InstrumentToken))
            .ToList();

        if (newInstruments.Any())
            await Set.AddRangeAsync(newInstruments, ct);
    }

    public async Task<HashSet<int>> GetAllTokensAsync(CancellationToken ct)
    => (await _db.Instruments
        .Select(i => i.InstrumentToken)
        .ToListAsync(ct))
        .ToHashSet();
}
