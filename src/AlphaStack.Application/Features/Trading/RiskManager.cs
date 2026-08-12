using Microsoft.Extensions.Logging;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Domain.Entities;

namespace AlphaStack.Application.Features.Trading;

/// <summary>
/// Enforces capital and risk rules before any entry trade is created.
/// All rules checked in order — first failure short-circuits.
/// </summary>
public class RiskManager : IRiskManager
{
    private const int MaxOpenTrades = 2;

    // Strategy type strings — must match StrategyDefinition.StrategyType exactly
    // (StrategyEngineFactory resolves by exact string match; a typo here fails
    // silently, matching an existing gotcha noted in APPLICATION_REFERENCE.md).
    private const string BullPutSpreadType = "BullPutSpread";
    private const string BearCallSpreadType = "BearCallSpread";
    private const string NiftyIronCondorType = "NiftyIronCondor";

    private readonly ITradeRepository _tradeRepository;
    private readonly IStrategyExecutionRepository _executionRepository;
    private readonly ILogger<RiskManager> _logger;

    public RiskManager(
        ITradeRepository tradeRepository,
        IStrategyExecutionRepository executionRepository,
        ILogger<RiskManager> logger)
    {
        _tradeRepository = tradeRepository;
        _executionRepository = executionRepository;
        _logger = logger;
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

        // Rule 2 — Max open trades (per execution)
        var openTrades = await _tradeRepository.GetOpenByExecutionAsync(execution.Id, ct);
        if (openTrades.Count >= MaxOpenTrades)
        {
            var reason = $"Max open trades reached ({openTrades.Count}/{MaxOpenTrades}). " +
                         "Close existing trades before entering a new one.";
            _logger.LogWarning("[RiskManager] BLOCKED | ExecutionId={Id} | {Reason}", execution.Id, reason);
            return RiskValidationResult.Reject(reason);
        }

        // Rule 3 — Combined drawdown guard (realized + unrealized, across every
        // running strategy for this user in the same Mode). Added Aug 2026:
        // per-execution drawdown alone can't catch the case where each strategy
        // is individually within its own limit but the user's combined exposure —
        // including a gap-through-stop-loss loss not yet caught by the 1-min exit
        // cycle — has already breached what they're willing to lose in total.
        var maxLoss = user.TotalCapitalAllocated * (user.MaxDrawdownPercent / 100m);
        var userExecutions = await _executionRepository.GetByUserAsync(execution.UserProfileId, ct);

        var combinedPnL = userExecutions
            .Where(e => e.Mode == execution.Mode)
            .Sum(e => e.TotalPnL);

        if (combinedPnL < -maxLoss)
        {
            var reason = $"Combined drawdown across all strategies breached. " +
                         $"Loss ₹{Math.Abs(combinedPnL):F0} (realized + unrealized, all running " +
                         $"{execution.Mode} strategies) exceeds limit ₹{maxLoss:F0} " +
                         $"({user.MaxDrawdownPercent}% of ₹{user.TotalCapitalAllocated:F0})";
            _logger.LogWarning("[RiskManager] BLOCKED | ExecutionId={Id} | {Reason}", execution.Id, reason);
            return RiskValidationResult.Reject(reason);
        }

        // Rule 4 — Iron Condor correlation gate. IC is directionally neutral but
        // still short-premium on the same underlying as BullPutSpread and
        // BearCallSpread. If both directional spreads are already open at once,
        // adding IC on top isn't diversification — RESEARCH_LOG.md's own
        // BPS+BCS-combined test found this pairing is NOT a clean hedge (real-only
        // sum +₹2,003 vs hypothetical combined -₹2,106, higher volatility, not
        // lower). Only applies when *this* entry is for NiftyIronCondor itself —
        // does not block BPS or BCS from opening independently of each other.
        var strategyType = execution.StrategyDefinition?.StrategyType;
        if (strategyType == NiftyIronCondorType)
        {
            var bpsExecution = userExecutions.FirstOrDefault(e =>
                e.Mode == execution.Mode &&
                e.StrategyDefinition?.StrategyType == BullPutSpreadType);
            var bcsExecution = userExecutions.FirstOrDefault(e =>
                e.Mode == execution.Mode &&
                e.StrategyDefinition?.StrategyType == BearCallSpreadType);

            var bpsOpen = bpsExecution is not null &&
                (await _tradeRepository.GetOpenByExecutionAsync(bpsExecution.Id, ct)).Count > 0;
            var bcsOpen = bcsExecution is not null &&
                (await _tradeRepository.GetOpenByExecutionAsync(bcsExecution.Id, ct)).Count > 0;

            if (bpsOpen && bcsOpen)
            {
                var reason = "Both BullPutSpread and BearCallSpread already open — " +
                              "skipping Iron Condor entry (not a clean hedge combination, " +
                              "see RESEARCH_LOG.md).";
                _logger.LogWarning("[RiskManager] BLOCKED | ExecutionId={Id} | {Reason}", execution.Id, reason);
                return RiskValidationResult.Reject(reason);
            }
        }

        _logger.LogInformation(
            "[RiskManager] ALLOWED | ExecutionId={Id} | Capital=₹{Cap:F0} | OpenTrades={Open} | " +
            "CombinedPnL=₹{PnL:F0}",
            execution.Id, estimatedTradeCapital, openTrades.Count, combinedPnL);

        return RiskValidationResult.Allow();
    }
}