using AlphaStack.Application.Common.Interfaces;

namespace AlphaStack.Infrastructure.ExternalServices.KiteConnect.Strategies;

public class StrategyEngineFactory : IStrategyEngineFactory
{
    private readonly IEnumerable<IStrategyEngine> _engines;

    public StrategyEngineFactory(IEnumerable<IStrategyEngine> engines)
        => _engines = engines;

    public IStrategyEngine Resolve(string strategyType)
        => _engines.FirstOrDefault(e => e.StrategyType == strategyType)
           ?? throw new InvalidOperationException(
               $"No strategy engine registered for type '{strategyType}'. " +
               $"Available: {string.Join(", ", _engines.Select(e => e.StrategyType))}");
}
