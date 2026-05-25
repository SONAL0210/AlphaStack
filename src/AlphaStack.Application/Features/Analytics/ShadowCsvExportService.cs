using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Domain.Entities;

namespace AlphaStack.Application.Features.Analytics;

/// <summary>
/// Exports ShadowTrade variants to CSV for parameter optimisation analysis.
///
/// Each real entry signal generates ~180 rows covering:
///   5 ADR multipliers × 4 spread widths × 3 profit targets × 3 stop losses
///
/// Use Excel/Python to slice by VixRegime, AdrMultiplier, SpreadWidth
/// to find the optimal parameter combination.
///
/// CSV written to: /app/exports/shadow_trades_{yyyyMMdd_HHmm}.csv
/// </summary>
public class ShadowCsvExportService
{
    private readonly IShadowTradeRepository _shadowRepo;
    private readonly ILogger<ShadowCsvExportService> _logger;

    private static readonly string ExportDirectory =
        Path.Combine(AppContext.BaseDirectory, "exports");

    public ShadowCsvExportService(
        IShadowTradeRepository shadowRepo,
        ILogger<ShadowCsvExportService> logger)
    {
        _shadowRepo = shadowRepo;
        _logger     = logger;
    }

    /// <summary>
    /// Exports all shadow trades (open + closed) to a timestamped CSV file.
    /// Returns file path on success, empty string if no data.
    /// </summary>
    public async Task<string> ExportAllAsync(CancellationToken ct = default)
    {
        var records = await _shadowRepo.GetAllAsync(ct);

        if (records.Count == 0)
        {
            _logger.LogInformation("[ShadowCsvExport] No shadow trades to export.");
            return string.Empty;
        }

        Directory.CreateDirectory(ExportDirectory);

        var ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        var timestamp = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ist)
                .ToString("yyyyMMdd_HHmm");
        var filePath  = Path.Combine(ExportDirectory, $"shadow_trades_{timestamp}.csv");

        await using var writer = new StreamWriter(filePath, false, Encoding.UTF8);

        await writer.WriteLineAsync(CsvHeader());

        foreach (var r in records)
            await writer.WriteLineAsync(ToCsvRow(r));

        _logger.LogInformation(
            "[ShadowCsvExport] Exported {Count} shadow trades → {Path}",
            records.Count, filePath);

        return filePath;
    }

    /// <summary>
    /// Builds a quick text summary of shadow trade performance by parameter bucket.
    /// Used by Telegram /shadowexport command before sending the CSV path.
    /// </summary>
    public async Task<string> BuildSummaryAsync(CancellationToken ct = default)
    {
        var records = await _shadowRepo.GetAllAsync(ct);

        if (records.Count == 0)
            return "No shadow trades logged yet. Shadow logging starts after the next entry signal.";

        var closed  = records.Where(r => r.Outcome != "Open").ToList();
        var open    = records.Count(r => r.Outcome == "Open");
        var wins    = closed.Count(r => r.Outcome == "Win");
        var losses  = closed.Count(r => r.Outcome == "Loss");
        var winRate = closed.Count > 0 ? wins * 100m / closed.Count : 0m;
        var totalPnL = closed.Sum(r => r.GrossPnL ?? 0m);

        // Best performing ADR multiplier by avg PnL
        var bestAdr = closed
            .GroupBy(r => r.AdrMultiplierUsed)
            .Select(g => (Mult: g.Key, AvgPnL: g.Average(r => r.GrossPnL ?? 0m), Count: g.Count()))
            .OrderByDescending(x => x.AvgPnL)
            .FirstOrDefault();

        // Best performing spread width by avg PnL
        var bestWidth = closed
            .GroupBy(r => r.SpreadWidth)
            .Select(g => (Width: g.Key, AvgPnL: g.Average(r => r.GrossPnL ?? 0m), Count: g.Count()))
            .OrderByDescending(x => x.AvgPnL)
            .FirstOrDefault();

        // Best VIX regime by win rate
        var bestVix = closed
            .GroupBy(r => r.VixRegime)
            .Select(g => (
                Regime: g.Key,
                WinRate: g.Count() > 0 ? g.Count(r => r.Outcome == "Win") * 100m / g.Count() : 0m,
                Count: g.Count()))
            .OrderByDescending(x => x.WinRate)
            .FirstOrDefault();

        return
            $"🔬 *Shadow Trade Analysis*\n\n" +
            $"Total variants: {records.Count}\n" +
            $"Closed: {closed.Count} | Open: {open}\n" +
            $"Wins: {wins} | Losses: {losses}\n" +
            $"Win rate: {winRate:F1}%\n" +
            $"Total gross P&L: ₹{totalPnL:F0}\n\n" +
            $"📊 *Best ADR Multiplier:* {bestAdr.Mult}x\n" +
            $"   Avg P&L ₹{bestAdr.AvgPnL:F0} over {bestAdr.Count} trades\n\n" +
            $"📊 *Best Spread Width:* {bestWidth.Width}pts\n" +
            $"   Avg P&L ₹{bestWidth.AvgPnL:F0} over {bestWidth.Count} trades\n\n" +
            $"📊 *Best VIX Regime:* {bestVix.Regime}\n" +
            $"   Win rate {bestVix.WinRate:F1}% over {bestVix.Count} trades\n\n" +
            $"Download full CSV via Swagger:\n" +
            $"`GET /api/analytics/shadow-export`";
    }

    // ── CSV structure ─────────────────────────────────────────────────────────

    private static string CsvHeader() =>
        "Id,RealSignalGroupId,WasRealTrade," +
        "StrategyName,EntryVariation," +
        "EvaluatedAt," +
        // Market context
        "SpotAtEntry,VixAtEntry,VixRegime,Ema20AtEntry," +
        "AdrAtEntry,AtrAtEntry,AtrAverageAtEntry,GapPercent," +
        "DaysToExpiry,ExpiryDate," +
        // Parameter variant
        "AdrMultiplierUsed,SpreadWidth,ProfitTargetPct,StopLossMultiplier," +
        // Strikes
        "ShortStrike,LongStrike,PremiumCollected," +
        "ProfitTargetRs,StopLossThresholdRs," +
        // Exit outcome
        "ExitReason,ExitDate,HoldingMinutes," +
        "PremiumAtExit,GrossPnL,Outcome";

    private static string ToCsvRow(ShadowTrade r)
    {
        var ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        var istTime = TimeZoneInfo.ConvertTimeFromUtc(r.EvaluatedAt, ist).ToString("yyyy-MM-dd HH:mm");
        return string.Join(",",
            Q(r.Id.ToString()),
            Q(r.RealSignalGroupId?.ToString() ?? ""),
            r.WasRealTrade ? "TRUE" : "FALSE",
            Q(r.StrategyName),
            Q(r.EntryVariation),
            Q(istTime),
            // Market context
            N(r.SpotAtEntry),
            N(r.VixAtEntry),
            Q(r.VixRegime),
            N(r.Ema20AtEntry),
            N(r.AdrAtEntry),
            N(r.AtrAtEntry),
            N(r.AtrAverageAtEntry),
            N(r.GapPercent),
            r.DaysToExpiry.ToString(),
            Q(r.ExpiryDate.ToString("yyyy-MM-dd")),
            // Parameter variant
            N(r.AdrMultiplierUsed),
            r.SpreadWidth.ToString(),
            N(r.ProfitTargetPct),
            N(r.StopLossMultiplier),
            // Strikes
            N(r.ShortStrike),
            N(r.LongStrike),
            N(r.PremiumCollected),
            N(r.ProfitTargetRs),
            N(r.StopLossThresholdRs),
            // Exit
            Q(r.ExitReason ?? ""),
            Q(r.ExitDate?.ToString("yyyy-MM-dd") ?? ""),
            N(r.HoldingMinutes),
            N(r.PremiumAtExit),
            N(r.GrossPnL),
            Q(r.Outcome));
    }

    private static string Q(string v) => $"\"{v.Replace("\"", "\"\"")}\"";
    private static string N(decimal? v) => v.HasValue
        ? v.Value.ToString("F2", CultureInfo.InvariantCulture) : "";
    private static string N(int? v) => v.HasValue ? v.Value.ToString() : "";
    private static string N(decimal v) => v.ToString("F2", CultureInfo.InvariantCulture);
}
