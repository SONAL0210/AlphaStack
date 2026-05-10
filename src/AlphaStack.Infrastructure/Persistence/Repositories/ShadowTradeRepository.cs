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
}
