using Microsoft.EntityFrameworkCore;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Domain.Entities;

namespace AlphaStack.Infrastructure.Persistence;

public partial class TradingDbContext : DbContext, IUnitOfWork
{
    public TradingDbContext(DbContextOptions<TradingDbContext> options) : base(options) { }

    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<StrategyDefinition> StrategyDefinitions => Set<StrategyDefinition>();
    public DbSet<StrategyExecution> StrategyExecutions => Set<StrategyExecution>();
    public DbSet<TradeOrder> TradeOrders => Set<TradeOrder>();
    public DbSet<Position> Positions => Set<Position>();
    public DbSet<Instrument> Instruments => Set<Instrument>();
    public DbSet<Trade> Trades => Set<Trade>();

    // ── NEW ──────────────────────────────────────────────────────────────────
    public DbSet<TradeAnalytics> TradeAnalytics => Set<TradeAnalytics>();
    public DbSet<ShadowTrade>     ShadowTrades    => Set<ShadowTrade>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TradingDbContext).Assembly);
    }

    public new async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => await base.SaveChangesAsync(cancellationToken);
        
    
}
