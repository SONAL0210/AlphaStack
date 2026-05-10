using Microsoft.Extensions.Logging;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Domain.Entities;

namespace AlphaStack.Application.Features.Trading;

/// <summary>
/// Enforces capital and risk rules before any entry trade is created.
/// All three rules checked in order — first failure short-circuits.
/// </summary>
public class RiskManager : IRiskManager
{
    private const int MaxOpenTrades = 2;

    private readonly ITradeRepository _tradeRepository;
    private readonly ILogger<RiskManager> _logger;

    public RiskManager(ITradeRepository tradeRepository, ILogger<RiskManager> logger)
    {
        _tradeRepository = tradeRepository;
        _logger          = logger;
    }

    public async Task<RiskValidationResult> ValidateEntryAsync(
        StrategyExecution execution,
        UserProfile user,
        decimal estimatedTradeCapital,
        CancellationToken ct = default)
    {
        // Rule 1 — Capital per trade
        var maxCapital = user.TotalCapitalAllocated * (user.MaxCapitalPerTradePercent / 100m);
        if (estimatedTradeCapital > maxCapital)
        {
            var reason = $"Trade capital ₹{estimatedTradeCapital:F0} exceeds max " +
                         $"₹{maxCapital:F0} ({user.MaxCapitalPerTradePercent}% of ₹{user.TotalCapitalAllocated:F0})";
            _logger.LogWarning("[RiskManager] BLOCKED | ExecutionId={Id} | {Reason}", execution.Id, reason);
            return RiskValidationResult.Reject(reason);
        }

        // Rule 2 — Max open trades
        var openTrades = await _tradeRepository.GetOpenByExecutionAsync(execution.Id, ct);
        if (openTrades.Count >= MaxOpenTrades)
        {
            var reason = $"Max open trades reached ({openTrades.Count}/{MaxOpenTrades}). " +
                         "Close existing trades before entering a new one.";
            _logger.LogWarning("[RiskManager] BLOCKED | ExecutionId={Id} | {Reason}", execution.Id, reason);
            return RiskValidationResult.Reject(reason);
        }

        // Rule 3 — Drawdown guard
        var maxLoss = user.TotalCapitalAllocated * (user.MaxDrawdownPercent / 100m);
        if (execution.RealizedPnL < -maxLoss)
        {
            var reason = $"Max drawdown breached. Loss ₹{Math.Abs(execution.RealizedPnL):F0} " +
                         $"exceeds limit ₹{maxLoss:F0} ({user.MaxDrawdownPercent}% of ₹{user.TotalCapitalAllocated:F0})";
            _logger.LogWarning("[RiskManager] BLOCKED | ExecutionId={Id} | {Reason}", execution.Id, reason);
            return RiskValidationResult.Reject(reason);
        }

        _logger.LogInformation(
            "[RiskManager] ALLOWED | ExecutionId={Id} | Capital=₹{Cap:F0} | OpenTrades={Open} | PnL=₹{PnL:F0}",
            execution.Id, estimatedTradeCapital, openTrades.Count, execution.RealizedPnL);

        return RiskValidationResult.Allow();
    }
}
