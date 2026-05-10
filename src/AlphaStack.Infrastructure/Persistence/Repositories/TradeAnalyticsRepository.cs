// ── Interface (add to IRepositories.cs) ──────────────────────────────────────
// Add this interface to your existing IRepositories.cs file:
//
// public interface ITradeAnalyticsRepository
// {
//     Task<TradeAnalytics?> GetByTradeIdAsync(Guid tradeId, CancellationToken ct = default);
//     Task AddAsync(TradeAnalytics analytics, CancellationToken ct = default);
//     Task UpdateAsync(TradeAnalytics analytics, CancellationToken ct = default);
//     Task<List<TradeAnalytics>> GetAllClosedAsync(CancellationToken ct = default);
// }

// ── Implementation (add to Repositories.cs) ───────────────────────────────────
// Add this class to your existing Repositories.cs file:

using Microsoft.EntityFrameworkCore;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Domain.Entities;
using AlphaStack.Infrastructure.Persistence;

namespace AlphaStack.Infrastructure.Persistence.Repositories;

public class TradeAnalyticsRepository : ITradeAnalyticsRepository
{
    private readonly TradingDbContext _db;

    public TradeAnalyticsRepository(TradingDbContext db) => _db = db;

    public async Task<TradeAnalytics?> GetByTradeIdAsync(Guid tradeId, CancellationToken ct = default)
        => await _db.TradeAnalytics.FirstOrDefaultAsync(x => x.TradeId == tradeId, ct);

    public async Task<List<TradeAnalytics>> GetAllClosedAsync(CancellationToken ct = default)
        => await _db.TradeAnalytics
            .Where(x => x.NetPnL != null)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);

    public async Task<List<TradeAnalytics>> GetAllAsync(CancellationToken ct = default)
        => await _db.TradeAnalytics
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);

    public async Task AddAsync(TradeAnalytics analytics, CancellationToken ct = default)
        => await _db.TradeAnalytics.AddAsync(analytics, ct);

    public Task UpdateAsync(TradeAnalytics analytics, CancellationToken ct = default)
    {
        _db.TradeAnalytics.Update(analytics);
        return Task.CompletedTask;
    }
}
