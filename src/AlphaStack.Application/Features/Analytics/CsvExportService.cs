using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Domain.Entities;
using AlphaStack.Domain.Enums;

namespace AlphaStack.Application.Features.Analytics;

/// <summary>
/// Exports TradeAnalytics to a CSV file for offline analysis in Excel/Python.
///
/// Usage (inject and call manually, or wire to an API endpoint):
///   var path = await _csvExport.ExportAsync();
///
/// CSV is written to:
///   /app/exports/trade_analytics_{yyyyMMdd_HHmm}.csv
///
/// Iron Condor rows are split into two logical rows (Put wing / Call wing) at
/// export time — mirroring how shadow_trades already stores IC as two rows
/// via shadow_group_id. TradeAnalytics itself stays one row per trade
/// (unchanged schema, per DECISIONS.md); the split only happens here, by
/// reading the underlying Position legs. See DECISIONS.md — "IC real-trade
/// Put/Call CSV split" for the design rationale.
/// </summary>
public class CsvExportService
{
    private readonly ITradeAnalyticsRepository _analyticsRepo;
    private readonly IPositionRepository _positionRepo;
    private readonly ILogger<CsvExportService> _logger;
    private static readonly string ExportFilePath = Path.Combine(AppContext.BaseDirectory, "exports", "trade_analytics.csv");

    private static readonly string ExportDirectory =
        Path.Combine(AppContext.BaseDirectory, "exports");

    public CsvExportService(
        ITradeAnalyticsRepository analyticsRepo,
        IPositionRepository positionRepo,
        ILogger<CsvExportService> logger)
    {
        _analyticsRepo = analyticsRepo;
        _positionRepo  = positionRepo;
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

        await writer.WriteLineAsync(CsvHeader());

        var rowCount = 0;
        foreach (var r in records)
        {
            foreach (var row in await BuildRowsAsync(r, ct))
            {
                await writer.WriteLineAsync(row);
                rowCount++;
            }
        }

        _logger.LogInformation(
            "[CsvExport] Exported {TradeCount} trades ({RowCount} CSV rows) → {Path}",
            records.Count, rowCount, ExportFilePath);

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

        var rowCount = 0;
        foreach (var r in records)
        {
            foreach (var row in await BuildRowsAsync(r, ct))
            {
                await writer.WriteLineAsync(row);
                rowCount++;
            }
        }

        _logger.LogInformation(
            "[CsvExport] Exported {TradeCount} trades ({RowCount} CSV rows) → {Path}",
            records.Count, rowCount, filePath);

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

    // ── Row building — handles the IC Put/Call split ─────────────────────────

    private static bool IsIronCondor(string strategyName) =>
        strategyName.Contains("IronCondor", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns one CSV row for a normal spread, or two rows (Put wing, Call
    /// wing) for an Iron Condor. Falls back to a single combined row (Leg
    /// blank) if the underlying positions can't be found or don't resolve
    /// into two clean 2-leg wings — never throws, never drops a trade from
    /// the export.
    /// </summary>
    private async Task<List<string>> BuildRowsAsync(TradeAnalytics r, CancellationToken ct)
    {
        if (!IsIronCondor(r.StrategyName))
            return [ToCsvRow(r, leg: "")];

        var legs = await _positionRepo.GetBySignalGroupAsync(r.TradeId, ct);

        var putLegs = legs.Where(p => p.OptionType == OptionType.Put).ToList();
        var callLegs = legs.Where(p => p.OptionType == OptionType.Call).ToList();

        if (putLegs.Count != 2 || callLegs.Count != 2)
        {
            // Couldn't cleanly resolve both wings (e.g. old data predating this
            // change, or a partially-filled/edge-case trade) — fall back to the
            // original combined row rather than silently dropping the trade.
            return [ToCsvRow(r, leg: "")];
        }

        var putRow = BuildWingRow(r, putLegs, "Put");
        var callRow = BuildWingRow(r, callLegs, "Call");

        return [putRow, callRow];
    }

    private static string BuildWingRow(TradeAnalytics r, List<Position> wingLegs, string leg)
    {
        var shortLeg = wingLegs.FirstOrDefault(p => p.Side == OrderSide.Sell);
        var longLeg  = wingLegs.FirstOrDefault(p => p.Side == OrderSide.Buy);

        var wingGrossPnl = wingLegs.Sum(p => p.RealizedPnL);
        var wingBrokerage = (r.Brokerage ?? 0) / 2;
        var wingNetPnl = wingGrossPnl - wingBrokerage;

        return ToCsvRow(
            r,
            leg,
            overrideShortStrike: shortLeg?.StrikePrice,
            overrideLongStrike: longLeg?.StrikePrice,
            overrideGrossPnl: wingGrossPnl,
            overrideBrokerage: wingBrokerage,
            overrideNetPnl: wingNetPnl);
    }

    // ── CSV Structure ─────────────────────────────────────────────────────────

    private static string CsvHeader() =>
        "TradeId,Leg," +
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
        "SlippageRs,ExecutionDelayMs,LotSize," +
        "Outcome";

    private static string ToCsvRow(
        TradeAnalytics r,
        string leg,
        decimal? overrideShortStrike = null,
        decimal? overrideLongStrike = null,
        decimal? overrideGrossPnl = null,
        decimal? overrideBrokerage = null,
        decimal? overrideNetPnl = null)
    {
        var entryDate = r.CreatedAt.ToLocalTime();
        var netPnl    = overrideNetPnl ?? r.NetPnL;
        var outcome   = netPnl.HasValue ? (netPnl >= 0 ? "Win" : "Loss") : "Open";

        return string.Join(",",
            Q(r.TradeId.ToString()),
            Q(leg),
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
            N(overrideShortStrike ?? r.ShortStrike),
            N(overrideLongStrike ?? r.LongStrike),
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
            N(overrideGrossPnl ?? r.GrossPnL),
            N(overrideBrokerage ?? r.Brokerage),
            N(netPnl),
            N(r.SlippageRs),
            N(r.ExecutionDelayMs),
            r.LotSize.ToString(),
            Q(outcome));
    }

    // Quote a string field (handles commas inside values)
    private static string Q(string v) => $"\"{v.Replace("\"", "\"\"")}\"";

    // Numeric field — blank if null
    private static string N(decimal? v) => v.HasValue
        ? v.Value.ToString("F2", CultureInfo.InvariantCulture) : "";
    private static string N(int? v)     => v.HasValue ? v.Value.ToString() : "";
}