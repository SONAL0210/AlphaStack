using AlphaStack.Application.Common.Interfaces;

namespace AlphaStack.Infrastructure.Persistence;

// TradingDbContext implements IUnitOfWork — SaveChangesAsync delegates to EF Core
public partial class TradingDbContext : IUnitOfWork
{
    // EF Core's SaveChangesAsync already matches the interface signature.
    // No additional code needed — the partial class just marks the interface.
}
