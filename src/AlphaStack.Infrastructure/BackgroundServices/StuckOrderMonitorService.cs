using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Domain.Enums;

namespace AlphaStack.Infrastructure.BackgroundServices;

/// <summary>
/// Monitors for orders stuck in Approved status for more than 10 minutes.
/// Runs every 5 minutes. Sends Telegram alert for each stuck order found.
/// </summary>
public class StuckOrderMonitorService : BackgroundService
{
    private static readonly TimeSpan CheckInterval  = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StuckThreshold = TimeSpan.FromMinutes(10);

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

        // GetPendingApprovalAsync returns Pending orders — reuse it and filter for Approved
        // (both are pre-fill states). Approved = Telegram approved but simulator not yet run.
        var pendingOrders = await orderRepo.GetPendingApprovalAsync(ct);

        var stuckOrders = pendingOrders
            .Where(o =>
                o.Status == OrderStatus.Approved &&
                o.ApprovalRequestedAt.HasValue &&
                DateTime.UtcNow - o.ApprovalRequestedAt.Value > StuckThreshold)
            .ToList();

        if (!stuckOrders.Any())
        {
            _logger.LogDebug("[StuckMonitor] No stuck orders found.");
            return;
        }

        _logger.LogWarning("[StuckMonitor] Found {Count} stuck order(s).", stuckOrders.Count);

        foreach (var order in stuckOrders)
        {
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
                    $"Status: {order.Status} for >{StuckThreshold.TotalMinutes} minutes\n" +
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
