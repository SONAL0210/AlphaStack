using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Domain.Enums;

namespace AlphaStack.Infrastructure.BackgroundServices;

/// <summary>
/// Runs every 5 minutes. Three checks against orders in pre-fill states:
///   1. Approved for >10 min — alert (Telegram approved, simulator should've fired fast)
///   2. Pending for >30 min — alert (nobody has responded yet)
///   3. Pending for >6 hours — auto-cancel (order missed its window entirely;
///      no re-evaluation needed since it was never approved)
/// </summary>
public class StuckOrderMonitorService : BackgroundService
{
    private static readonly TimeSpan CheckInterval           = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ApprovedStuckThreshold  = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PendingStuckThreshold   = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan PendingCancelThreshold  = TimeSpan.FromHours(6);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StuckOrderMonitorService> _logger;

    public StuckOrderMonitorService(
        IServiceScopeFactory scopeFactory,
        ILogger<StuckOrderMonitorService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[StuckMonitor] Service started. Checking every {Interval} min.",
            CheckInterval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckForStuckOrdersAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StuckMonitor] Unexpected error during check.");
            }

            await Task.Delay(CheckInterval, stoppingToken);
        }
    }

    private async Task CheckForStuckOrdersAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();

        var orderRepo     = scope.ServiceProvider.GetRequiredService<ITradeOrderRepository>();
        var executionRepo = scope.ServiceProvider.GetRequiredService<IStrategyExecutionRepository>();
        var userRepo      = scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();
        var telegram      = scope.ServiceProvider.GetRequiredService<ITelegramNotificationService>();
        var encryption    = scope.ServiceProvider.GetRequiredService<IEncryptionService>();
        var uow           = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var candidates = await orderRepo.GetPendingApprovalAsync(ct);

        // ── Check 3: Pending > 6 hours — auto-cancel first, so cancelled orders
        // never also get swept into the alert loop below in the same cycle ──

        var toCancel = candidates
            .Where(o =>
                o.Status == OrderStatus.Pending &&
                o.ApprovalRequestedAt.HasValue &&
                DateTime.UtcNow - o.ApprovalRequestedAt.Value > PendingCancelThreshold)
            .ToList();

        if (toCancel.Any())
        {
            _logger.LogWarning(
                "[StuckMonitor] Found {Count} Pending order(s) past {Threshold}hr — auto-cancelling.",
                toCancel.Count, PendingCancelThreshold.TotalHours);

            foreach (var order in toCancel)
            {
                _logger.LogWarning(
                    "[StuckMonitor] Auto-cancelling stale order | OrderId={OrderId} Symbol={Symbol} " +
                    "PendingSince={Since} Age={Age:F0}min",
                    order.Id, order.TradingSymbol, order.ApprovalRequestedAt,
                    (DateTime.UtcNow - order.ApprovalRequestedAt!.Value).TotalMinutes);

                order.Cancel();
                await orderRepo.UpdateAsync(order, ct);
            }

            await uow.SaveChangesAsync(ct);
        }

        var cancelledIds = toCancel.Select(o => o.Id).ToHashSet();

        // ── Checks 1 & 2: Approved > 10min, Pending > 30min — alert only ──

        var stuckOrders = candidates
            .Where(o =>
                !cancelledIds.Contains(o.Id) &&
                o.ApprovalRequestedAt.HasValue &&
                ((o.Status == OrderStatus.Approved &&
                  DateTime.UtcNow - o.ApprovalRequestedAt.Value > ApprovedStuckThreshold) ||
                 (o.Status == OrderStatus.Pending &&
                  DateTime.UtcNow - o.ApprovalRequestedAt.Value > PendingStuckThreshold)))
            .ToList();

        if (!stuckOrders.Any())
        {
            _logger.LogDebug("[StuckMonitor] No stuck orders found.");
            return;
        }

        _logger.LogWarning("[StuckMonitor] Found {Count} stuck order(s).", stuckOrders.Count);

        foreach (var order in stuckOrders)
        {
            var threshold = order.Status == OrderStatus.Approved
                ? ApprovedStuckThreshold
                : PendingStuckThreshold;

            _logger.LogError(
                "[StuckMonitor] STUCK ORDER | OrderId={OrderId} Symbol={Symbol} Status={Status} " +
                "ApprovedAt={ApprovedAt} StuckFor={StuckFor}min",
                order.Id, order.TradingSymbol, order.Status,
                order.ApprovalRequestedAt,
                (int)(DateTime.UtcNow - order.ApprovalRequestedAt!.Value).TotalMinutes);

            try
            {
                var execution = await executionRepo.GetByIdAsync(order.StrategyExecutionId, ct);
                if (execution is null) continue;

                var user = await userRepo.GetByIdAsync(execution.UserProfileId, ct);
                if (user is null) continue;

                var botToken = encryption.Decrypt(user.EncryptedTelegramBotToken);

                await telegram.SendMessageAsync(
                    botToken,
                    user.TelegramChatId,
                    $"🚨 *STUCK ORDER ALERT*\n\n" +
                    $"Symbol: `{order.TradingSymbol}`\n" +
                    $"Side: {order.Side}\n" +
                    $"Status: {order.Status} for >{threshold.TotalMinutes} minutes\n" +
                    $"OrderId: `{order.Id}`\n\n" +
                    $"Manual intervention required.",
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[StuckMonitor] Failed to send alert for order {OrderId}", order.Id);
            }
        }
    }
}