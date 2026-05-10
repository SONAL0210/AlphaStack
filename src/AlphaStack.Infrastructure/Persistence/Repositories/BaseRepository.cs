using Microsoft.EntityFrameworkCore;
using AlphaStack.Domain.Common;

namespace AlphaStack.Infrastructure.Persistence.Repositories;

public abstract class BaseRepository<T> where T : BaseEntity
{
    protected readonly TradingDbContext _db;

    protected BaseRepository(TradingDbContext db) => _db = db;

    protected DbSet<T> Set => _db.Set<T>();

    public async Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await Set.FindAsync([id], ct);

    public async Task AddAsync(T entity, CancellationToken ct = default)
        => await Set.AddAsync(entity, ct);

    public Task UpdateAsync(T entity, CancellationToken ct = default)
    {
        _db.Entry(entity).State = EntityState.Modified;
        return Task.CompletedTask;
    }
}
