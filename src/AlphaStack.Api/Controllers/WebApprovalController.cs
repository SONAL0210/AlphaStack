using Microsoft.AspNetCore.Mvc;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Application.Features.Trading;
using AlphaStack.Domain.Enums;

namespace AlphaStack.API.Controllers;

/// <summary>
/// Fallback approval UI when Telegram is unreachable.
/// No auth — URL obscurity is sufficient for paper trading.
/// Access: https://alphastack.duckdns.org/approve
/// </summary>
[Route("approve")]
public class WebApprovalController : Controller
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WebApprovalController> _logger;

    private const string IndexCss = """
        body { font-family: system-ui, sans-serif; background: #0f1117; color: #e0e0e0; 
                max-width: 480px; margin: 0 auto; padding: 16px; }
        h1 { color: #4ade80; font-size: 1.2rem; margin-bottom: 4px; }
        .time { color: #666; font-size: 0.8rem; margin-bottom: 20px; }
        .card { background: #1a1d27; border: 1px solid #2a2d3a; border-radius: 8px; 
                padding: 16px; margin-bottom: 16px; }
        .strategy { font-weight: bold; color: #60a5fa; font-size: 1.1rem; margin-bottom: 8px; }
        .meta { font-size: 0.9rem; line-height: 1.8; color: #ccc; }
        .meta code { background: #2a2d3a; padding: 2px 6px; border-radius: 4px; 
                    font-size: 0.75rem; word-break: break-all; }
        .age { color: #f59e0b; font-size: 0.8rem; }
        .actions { margin-top: 14px; display: flex; gap: 10px; }
        .btn { padding: 10px 20px; border: none; border-radius: 6px; font-size: 1rem; 
                cursor: pointer; font-weight: bold; }
        .approve { background: #16a34a; color: white; }
        .approve:hover { background: #15803d; }
        .reject { background: #dc2626; color: white; }
        .reject:hover { background: #b91c1c; }
        .empty { text-align: center; color: #666; padding: 40px; }
    """;

    private const string ResultCss = """
        body { font-family: system-ui; background: #0f1117; color: #e0e0e0; 
                max-width: 480px; margin: 40px auto; padding: 16px; text-align: center; }
        .result { background: #1a1d27; border-radius: 8px; padding: 30px; }
        h2 { font-size: 1.4rem; margin-bottom: 12px; }
        a { color: #60a5fa; }
    """;

    public WebApprovalController(
        IServiceScopeFactory scopeFactory,
        ILogger<WebApprovalController> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var orderRepo    = scope.ServiceProvider.GetRequiredService<ITradeOrderRepository>();
        var analyticsRepo = scope.ServiceProvider.GetRequiredService<ITradeAnalyticsRepository>();

        var allPending = await orderRepo.GetPendingApprovalAsync(ct);

        // Group by signal group
        var groups = allPending
            .GroupBy(o => o.SignalGroupId)
            .ToList();

        var cards = new System.Text.StringBuilder();

        foreach (var group in groups)
        {
            var analytics = await analyticsRepo.GetByTradeIdAsync(group.Key, ct);
            var firstOrder = group.First();
            var age = (int)(DateTime.UtcNow - firstOrder.CreatedAt).TotalMinutes;
            var ageWarning = age > 30 ? $"⚠️ {age} min old" : $"{age} min ago";

            cards.Append($"""
                <div class="card">
                    <div class="strategy">{analytics?.StrategyName ?? "Unknown"} — {analytics?.EntryVariation ?? ""}</div>
                    <div class="meta">
                        Short: {analytics?.ShortStrike:F0} | Long: {analytics?.LongStrike:F0}<br/>
                        Credit: ₹{analytics?.PremiumCollected:F2}/unit = <strong>₹{(analytics?.PremiumCollected ?? 0) * 65:F0}</strong><br/>
                        Capital at risk: ₹{analytics?.CapitalAtRisk:F0} ({analytics?.CapitalAtRiskPercent:F1}%)<br/>
                        VIX: {analytics?.VixAtEntry:F1} | Spot: {analytics?.SpotAtEntry:F0} | {analytics?.MarketRegime}<br/>
                        Signal: <code>{group.Key}</code><br/>
                        <span class="age">{ageWarning}</span>
                    </div>
                    <div class="actions">
                        <form method="post" action="/approve/{group.Key}/approve" style="display:inline">
                            <button class="btn approve" onclick="return confirm('Approve this trade?')">✅ APPROVE</button>
                        </form>
                        <form method="post" action="/approve/{group.Key}/reject" style="display:inline">
                            <button class="btn reject" onclick="return confirm('Reject this trade?')">❌ REJECT</button>
                        </form>
                    </div>
                </div>
            """);
        }

        if (!groups.Any())
            cards.Append("<div class='empty'>No pending signals right now.</div>");

        var istNow = DateTime.UtcNow.AddHours(5).AddMinutes(30).ToString("HH:mm");
        var cardsHtml = cards.ToString();

        var html = $"""
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8"/>
                <meta name="viewport" content="width=device-width, initial-scale=1"/>
                <title>AlphaStack — Approvals</title>
                <style>{IndexCss}</style>
                <meta http-equiv="refresh" content="60"/>
            </head>
            <body>
                <h1>⚡ AlphaStack — Pending Approvals</h1>
                <div class="time">Auto-refreshes every 60s | {istNow} IST</div>
                {cardsHtml}
            </body>
            </html>
        """;

        return Content(html, "text/html");
    }

    [HttpPost("{signalGroupId:guid}/approve")]
    public async Task<IActionResult> Approve(Guid signalGroupId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var orderRepo = scope.ServiceProvider.GetRequiredService<ITradeOrderRepository>();
        var paperSim  = scope.ServiceProvider.GetRequiredService<PaperOrderSimulator>();
        var uow       = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var groupOrders = await orderRepo.GetBySignalGroupAsync(signalGroupId, ct);
        var pending     = groupOrders.Where(o => o.Status == OrderStatus.Pending).ToList();

        if (!pending.Any())
            return Content(ResultHtml("⚠️ Already processed", "This signal was already handled."), "text/html");

        try
        {
            // Step 1 — mark orders Approved and save
            foreach (var order in pending)
            {
                order.Approve();
                await orderRepo.UpdateAsync(order, ct);
            }
            await uow.SaveChangesAsync(ct);

            // Step 2 — simulate fills (fetches approved orders internally)
            var success = await paperSim.SimulateEntryFillAsync(signalGroupId, ct);

            _logger.LogInformation(
                "[WebApproval] Approved signal {GroupId} via web UI — fills success={Success}",
                signalGroupId, success);

            var msg = success
                ? "Paper fills executed. Position tracking started."
                : "Approval saved but some fills failed — check logs.";

            return Content(ResultHtml("✅ Trade Approved", msg), "text/html");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WebApproval] Approval failed for {GroupId}", signalGroupId);
            return Content(ResultHtml("❌ Error", $"Approval failed: {ex.Message}"), "text/html");
        }
    }

    [HttpPost("{signalGroupId:guid}/reject")]
    public async Task<IActionResult> Reject(Guid signalGroupId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var orderRepo   = scope.ServiceProvider.GetRequiredService<ITradeOrderRepository>();
        var uow         = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var groupOrders = await orderRepo.GetBySignalGroupAsync(signalGroupId, ct);
        var pending     = groupOrders.Where(o => o.Status == OrderStatus.Pending).ToList();

        if (!pending.Any())
            return Content(ResultHtml("⚠️ Already processed", "This signal was already handled."), "text/html");

        foreach (var order in pending)
        {
            order.Reject();
            await orderRepo.UpdateAsync(order, ct);
        }
        await uow.SaveChangesAsync(ct);

        _logger.LogInformation("[WebApproval] Rejected signal {GroupId} via web UI", signalGroupId);
        return Content(ResultHtml("❌ Trade Rejected", "Signal rejected. No position created."), "text/html");
    }

    private static string ResultHtml(string title, string message) => $"""
        <!DOCTYPE html>
        <html>
        <head>
            <meta charset="utf-8"/>
            <meta name="viewport" content="width=device-width, initial-scale=1"/>
            <title>AlphaStack</title>
            <style>{ResultCss}</style>
        </head>
        <body>
            <div class="result">
                <h2>{title}</h2>
                <p>{message}</p>
                <a href="/approve">← Back to approvals</a>
            </div>
        </body>
        </html>
    """;
}