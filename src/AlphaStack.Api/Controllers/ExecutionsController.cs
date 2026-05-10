using Microsoft.AspNetCore.Mvc;
using AlphaStack.Application.Common.Interfaces;

namespace AlphaStack.API.Controllers;

[ApiController]
[Route("api/executions")]
public class ExecutionsController : ControllerBase
{
    private readonly IStrategyExecutionRepository _executionRepo;
    private readonly IPositionRepository _positionRepo;
    private readonly IUnitOfWork _uow;

    public ExecutionsController(
        IStrategyExecutionRepository executionRepo,
        IPositionRepository positionRepo,
        IUnitOfWork uow)
    {
        _executionRepo = executionRepo;
        _positionRepo = positionRepo;
        _uow = uow;
    }

    /// <summary>
    /// Get all executions for a user with current P&L summary.
    /// GET /api/executions?userId={guid}
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetByUser([FromQuery] Guid userId, CancellationToken ct)
    {
        var executions = await _executionRepo.GetByUserAsync(userId, ct);

        var result = executions.Select(e => new ExecutionSummaryDto(
            ExecutionId: e.Id,
            StrategyDefinitionId: e.StrategyDefinitionId,
            Mode: e.Mode.ToString(),
            IsRunning: e.IsRunning,
            AllocatedCapital: e.AllocatedCapital,
            RealizedPnL: e.RealizedPnL,
            UnrealizedPnL: e.UnrealizedPnL,
            TotalPnL: e.TotalPnL,
            TotalTrades: e.TotalTradesCount,
            WinRate: e.WinRate,
            StartedAt: e.StartedAt
        ));

        return Ok(result);
    }

    /// <summary>
    /// Get open positions for an execution with live unrealized P&L.
    /// GET /api/executions/{executionId}/positions
    /// </summary>
    [HttpGet("{executionId:guid}/positions")]
    public async Task<IActionResult> GetOpenPositions(Guid executionId, CancellationToken ct)
    {
        var positions = await _positionRepo.GetOpenByExecutionAsync(executionId, ct);

        var result = positions.Select(p => new PositionDto(
            Id: p.Id,
            SignalGroupId: p.SignalGroupId,
            TradingSymbol: p.TradingSymbol,
            OptionType: p.OptionType?.ToString(),
            StrikePrice: p.StrikePrice,
            ExpiryDate: p.ExpiryDate,
            Side: p.Side.ToString(),
            Quantity: p.Quantity,
            EntryPrice: p.EntryPrice,
            CurrentPrice: p.CurrentPrice,
            UnrealizedPnL: p.UnrealizedPnL,
            OpenedAt: p.OpenedAt
        ));

        return Ok(result);
    }

    /// <summary>
    /// Start an execution — background services will pick it up on next cycle.
    /// POST /api/executions/{executionId}/start
    /// </summary>
    [HttpPost("{executionId:guid}/start")]
    public async Task<IActionResult> Start(Guid executionId, CancellationToken ct)
    {
        var execution = await _executionRepo.GetByIdAsync(executionId, ct);
        if (execution is null) return NotFound();
        if (execution.IsRunning) return BadRequest(new { error = "Execution is already running." });

        execution.Start();
        await _executionRepo.UpdateAsync(execution, ct);
        await _uow.SaveChangesAsync(ct);

        return Ok(new { message = "Execution started.", executionId });
    }

    /// <summary>
    /// Stop an execution — background services will skip it on next cycle.
    /// POST /api/executions/{executionId}/stop
    /// </summary>
    [HttpPost("{executionId:guid}/stop")]
    public async Task<IActionResult> Stop(Guid executionId, CancellationToken ct)
    {
        var execution = await _executionRepo.GetByIdAsync(executionId, ct);
        if (execution is null) return NotFound();
        if (!execution.IsRunning) return BadRequest(new { error = "Execution is not running." });

        execution.Stop();
        await _executionRepo.UpdateAsync(execution, ct);
        await _uow.SaveChangesAsync(ct);

        return Ok(new { message = "Execution stopped.", executionId });
    }
}

public record ExecutionSummaryDto(
    Guid ExecutionId,
    Guid StrategyDefinitionId,
    string Mode,
    bool IsRunning,
    decimal AllocatedCapital,
    decimal RealizedPnL,
    decimal UnrealizedPnL,
    decimal TotalPnL,
    int TotalTrades,
    decimal WinRate,
    DateTime? StartedAt);

public record PositionDto(
    Guid Id,
    Guid SignalGroupId,
    string TradingSymbol,
    string? OptionType,
    decimal? StrikePrice,
    DateOnly? ExpiryDate,
    string Side,
    int Quantity,
    decimal EntryPrice,
    decimal CurrentPrice,
    decimal UnrealizedPnL,
    DateTime OpenedAt);
