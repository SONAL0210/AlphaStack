using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Domain.Entities;

namespace AlphaStack.Application.Features.Analytics;

/// <summary>
/// Exports TradeAnalytics to a CSV file for offline analysis in Excel/Python.
///
/// Usage (inject and call manually, or wire to an API endpoint):
///   var path = await _csvExport.ExportAsync();
///
/// CSV is written to:
///   /app/exports/trade_analytics_{yyyyMMdd_HHmm}.csv
/// </summary>
public class CsvExportService
{
    private readonly ITradeAnalyticsRepository _analyticsRepo;
    private readonly ILogger<CsvExportService> _logger;
    private static readonly string ExportFilePath = Path.Combine(AppContext.BaseDirectory, "exports", "trade_analytics.csv");

    private static readonly string ExportDirectory =
        Path.Combine(AppContext.BaseDirectory, "exports");

    public CsvExportService(
        ITradeAnalyticsRepository analyticsRepo,
        ILogger<CsvExportService> logger)
    {
        _analyticsRepo = analyticsRepo;
        _logger        = logger;
    }

    /// <summary>
    /// Exports all closed trades to CSV. Returns the file path on success.
    /// </summary>
    public async Task<string> ExportClosedTradesAsync(CancellationToken ct = default)
    {
        
        var records = await _analyticsRepo.GetAllClosedAsync(ct);

        if (records.Count == 0)
        {
            _logger.LogInformation("[CsvExport] No closed trades to export.");
            return string.Empty;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ExportFilePath)!);
        await using var writer = new StreamWriter(ExportFilePath, false, Encoding.UTF8);        

        // Header row
        await writer.WriteLineAsync(CsvHeader());

        // Data rows
        foreach (var r in records)
            await writer.WriteLineAsync(ToCsvRow(r));

        _logger.LogInformation(
            "[CsvExport] Exported {Count} trades → {Path}", records.Count, ExportFilePath);

        return ExportFilePath;
    }

    /// <summary>
    /// Exports ALL trades (open + closed) — useful for mid-session snapshots.
    /// </summary>
    public async Task<string> ExportAllTradesAsync(CancellationToken ct = default)
    {
        var records = await _analyticsRepo.GetAllAsync(ct);

        if (records.Count == 0)
        {
            _logger.LogInformation("[CsvExport] No trades to export.");
            return string.Empty;
        }

        Directory.CreateDirectory(ExportDirectory);

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
        var filePath  = Path.Combine(ExportDirectory, $"trade_analytics_all_{timestamp}.csv");

        await using var writer = new StreamWriter(filePath, false, Encoding.UTF8);

        await writer.WriteLineAsync(CsvHeader());

        foreach (var r in records)
            await writer.WriteLineAsync(ToCsvRow(r));

        _logger.LogInformation(
            "[CsvExport] Exported {Count} trades → {Path}", records.Count, filePath);

        return filePath;
    }

    public async Task<string> BuildLatestTradeSummaryAsync(CancellationToken ct = default)
    {
        var records = await _analyticsRepo.GetAllClosedAsync(ct);

        _logger.LogInformation(
            "[CsvExport] Closed trades fetched: {Count}",
            records.Count);

        if (records.Count == 0)
        {
            var allRecords = await _analyticsRepo.GetAllAsync(ct);

            _logger.LogWarning(
                "[CsvExport] No closed trades found. Total analytics records in DB: {Count}",
                allRecords.Count);

            return $"No closed trades found. Total records in DB: {allRecords.Count}";
        }

        // Sort by estimated close time instead of entry time.
        // CreatedAt alone can miss trades closed later if another trade was opened after it.
        var latest = records
            .OrderByDescending(x => x.CreatedAt.AddMinutes(x.HoldingMinutes ?? 0))
            .First();

        var outcome = latest.NetPnL.HasValue
            ? (latest.NetPnL >= 0 ? "Win" : "Loss")
            : "Open";

        return
            $"📊 Latest Trade\n" +
            $"Strategy: {latest.StrategyName}\n" +
            $"PnL: ₹{latest.NetPnL ?? 0:F2}\n" +
            $"Exit: {latest.ExitReason ?? "N/A"}\n" +
            $"Result: {outcome}";
    }

    public async Task<string> BuildPortfolioSummaryAsync(CancellationToken ct = default)
    {
        var records = await _analyticsRepo.GetAllClosedAsync(ct);

        if (records.Count == 0)
            return "No closed trades found.";

        var totalTrades = records.Count;
        var totalPnl = records.Sum(x => x.NetPnL ?? 0);
        var wins = records.Count(x => (x.NetPnL ?? 0) > 0);
        var losses = totalTrades - wins;
        var winRate = totalTrades > 0
            ? (wins * 100m / totalTrades)
            : 0;

        return
            $"📁 Portfolio Summary\n" +
            $"Trades: {totalTrades}\n" +
            $"Wins: {wins}\n" +
            $"Losses: {losses}\n" +
            $"Win Rate: {winRate:F1}%\n" +
            $"Total PnL: ₹{totalPnl:F2}";
    }

    // ── CSV Structure ─────────────────────────────────────────────────────────

    private static string CsvHeader() =>
        "TradeId," +
        "StrategyName,EntryVariation,ExitVariation," +
        "EntryDate,EntryTime,HoldingMinutes," +
        "MarketRegime,VixRegime,VixAtEntry," +
        "SpotAtEntry,SpotAtExit," +
        "EMA20AtEntry,ADRAtEntry,ATRAtEntry,ATRAvg,GapPercent," +
        "ShortStrike,LongStrike,SpreadWidth,StrikeDistanceInADR,ADRMultiplierUsed," +
        "DaysToExpiryAtEntry,ExpiryDate," +
        "PremiumCollected,PremiumCaptured," +
        "ProfitTargetRs,StopLossThresholdRs," +
        "CapitalAtRisk,CapitalAtRiskPercent," +
        "MaxMtmProfit,MaxMtmLoss," +
        "ExitReason,GrossPnL,Brokerage,NetPnL," +
        "SlippageRs,ExecutionDelayMs," +
        "Outcome,LotSize";

    private static string ToCsvRow(TradeAnalytics r)
    {
        var entryDate = r.CreatedAt.ToLocalTime();
        var outcome   = r.NetPnL.HasValue ? (r.NetPnL >= 0 ? "Win" : "Loss") : "Open";

        return string.Join(",",
            Q(r.TradeId.ToString()),
            Q(r.StrategyName),
            Q(r.EntryVariation),
            Q(r.ExitVariation ?? ""),
            Q(entryDate.ToString("yyyy-MM-dd")),
            Q(entryDate.ToString("HH:mm")),
            N(r.HoldingMinutes),
            Q(r.MarketRegime),
            Q(r.VixRegime),
            N(r.VixAtEntry),
            N(r.SpotAtEntry),
            N(r.SpotAtExit),
            N(r.Ema20AtEntry),
            N(r.AdrAtEntry),
            N(r.AtrAtEntry),
            N(r.AtrAverageAtEntry),
            N(r.GapPercent),
            N(r.ShortStrike),
            N(r.LongStrike),
            N(r.SpreadWidth),
            N(r.StrikeDistanceInAdr),
            N(r.AdrMultiplierUsed),
            N(r.DaysToExpiryAtEntry),
            Q(r.ExpiryDate.ToString("yyyy-MM-dd")),
            N(r.PremiumCollected),
            N(r.PremiumCaptured),
            N(r.ProfitTargetRs),
            N(r.StopLossThresholdRs),
            N(r.CapitalAtRisk),
            N(r.CapitalAtRiskPercent),
            N(r.MaxMtmProfit),
            N(r.MaxMtmLoss),
            Q(r.ExitReason ?? ""),
            N(r.GrossPnL),
            N(r.Brokerage),
            N(r.NetPnL),
            N(r.SlippageRs),
            N(r.ExecutionDelayMs),
            Q(outcome),
            r.LotSize.ToString());
    }

    // Quote a string field (handles commas inside values)
    private static string Q(string v) => $"\"{v.Replace("\"", "\"\"")}\"";

    // Numeric field — blank if null
    private static string N(decimal? v) => v.HasValue
        ? v.Value.ToString("F2", CultureInfo.InvariantCulture) : "";
    private static string N(int? v)     => v.HasValue ? v.Value.ToString() : "";
}
