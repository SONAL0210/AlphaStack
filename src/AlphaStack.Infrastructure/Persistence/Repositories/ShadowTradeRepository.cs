using Microsoft.EntityFrameworkCore;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Domain.Entities;
using AlphaStack.Infrastructure.Persistence;

namespace AlphaStack.Infrastructure.Repositories;

public class ShadowTradeRepository : IShadowTradeRepository
{
    private readonly TradingDbContext _db;

    public ShadowTradeRepository(TradingDbContext db)
    {
        _db = db;
    }

    public async Task AddRangeAsync(IEnumerable<ShadowTrade> trades, CancellationToken ct = default)
        => await _db.ShadowTrades.AddRangeAsync(trades, ct);

    public async Task<List<ShadowTrade>> GetOpenAsync(CancellationToken ct = default)
        => await _db.ShadowTrades
            .Where(s => s.Outcome == "Open")
            .ToListAsync(ct);

    public async Task UpdateAsync(ShadowTrade trade, CancellationToken ct = default)
    {
        _db.ShadowTrades.Update(trade);
        await Task.CompletedTask;
    }

    public async Task<List<ShadowTrade>> GetAllAsync(CancellationToken ct = default)
        => await _db.ShadowTrades
            .OrderByDescending(s => s.EvaluatedAt)
            .ToListAsync(ct);

    /// <summary>
    /// Average VixAtEntry over the last N distinct prior calendar days logged for this
    /// strategy, strictly before beforeDate. Used to compute VIX rate-of-change without
    /// depending on the market-data provider's historical-candle fetch (which is not
    /// confirmed to support the VIX instrument token correctly — see FyersMarketDataProvider
    /// gotcha: "defaults to NIFTY if config is missing" for unlisted tokens).
    /// Returns null if fewer than 2 distinct prior days exist (not enough history yet).
    /// </summary>
    public async Task<decimal?> GetRecentAvgVixAsync(
        string strategyName, DateTime beforeDate, int days, CancellationToken ct = default)
    {
        var recentDailyVix = await _db.ShadowTrades
            .Where(s => s.StrategyName == strategyName && s.EvaluatedAt < beforeDate.Date)
            .GroupBy(s => s.EvaluatedAt.Date)
            .Select(g => new { Date = g.Key, AvgVix = g.Average(s => s.VixAtEntry) })
            .OrderByDescending(g => g.Date)
            .Take(days)
            .ToListAsync(ct);

        return recentDailyVix.Count >= 2
            ? recentDailyVix.Average(x => x.AvgVix)
            : null;
    }
}
