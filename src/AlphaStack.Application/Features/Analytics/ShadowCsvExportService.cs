using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Domain.Entities;

namespace AlphaStack.Application.Features.Analytics;

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

    public async Task<string> ExportAllAsync(CancellationToken ct = default)
    {
        var records = await _shadowRepo.GetAllAsync(ct);

        if (records.Count == 0)
        {
            _logger.LogInformation("[ShadowCsvExport] No shadow trades to export.");
            return string.Empty;
        }

        Directory.CreateDirectory(ExportDirectory);

        var ist       = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
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

    public async Task<string> BuildSummaryAsync(CancellationToken ct = default)
    {
        var records = await _shadowRepo.GetAllAsync(ct);

        if (records.Count == 0)
            return "No shadow trades logged yet.";

        var closed  = records.Where(r => r.Outcome != "Open").ToList();
        var open    = records.Count(r => r.Outcome == "Open");

        // ── Per-strategy breakdown ────────────────────────────────────────────
        var byStrategy = closed
            .GroupBy(r => r.StrategyName)
            .Select(g => (
                Name:    g.Key,
                Wins:    g.Count(r => r.Outcome == "Win"),
                Losses:  g.Count(r => r.Outcome == "Loss"),
                WinRate: g.Count() > 0
                    ? g.Count(r => r.Outcome == "Win") * 100m / g.Count() : 0m,
                AvgPnL:  g.Average(r => r.GrossPnL ?? 0m)))
            .OrderBy(x => x.Name)
            .ToList();

        var stratLines = string.Join("\n", byStrategy.Select(s =>
            $"  {s.Name}: {s.WinRate:F0}% ({s.Wins}W/{s.Losses}L) avg ₹{s.AvgPnL:F0}"));

        // ── Best ADR multiplier per strategy ──────────────────────────────────
        var bestAdr = closed
            .GroupBy(r => new { r.StrategyName, r.AdrMultiplierUsed })
            .Select(g => (
                Strategy: g.Key.StrategyName,
                Mult:     g.Key.AdrMultiplierUsed,
                AvgPnL:   g.Average(r => r.GrossPnL ?? 0m),
                WinRate:  g.Count() > 0
                    ? g.Count(r => r.Outcome == "Win") * 100m / g.Count() : 0m,
                Count:    g.Count()))
            .OrderByDescending(x => x.AvgPnL)
            .FirstOrDefault();

        // ── Best spread width per strategy ────────────────────────────────────
        var bestWidth = closed
            .GroupBy(r => new { r.StrategyName, r.SpreadWidth })
            .Select(g => (
                Strategy: g.Key.StrategyName,
                Width:    g.Key.SpreadWidth,
                AvgPnL:   g.Average(r => r.GrossPnL ?? 0m),
                WinRate:  g.Count() > 0
                    ? g.Count(r => r.Outcome == "Win") * 100m / g.Count() : 0m,
                Count:    g.Count()))
            .OrderByDescending(x => x.AvgPnL)
            .FirstOrDefault();

        // ── Best VIX regime ───────────────────────────────────────────────────
        var bestVix = closed
            .GroupBy(r => new { r.StrategyName, r.VixRegime })
            .Select(g => (
                Strategy: g.Key.StrategyName,
                Regime:   g.Key.VixRegime,
                WinRate:  g.Count() > 0
                    ? g.Count(r => r.Outcome == "Win") * 100m / g.Count() : 0m,
                Count:    g.Count()))
            .OrderByDescending(x => x.WinRate)
            .FirstOrDefault();

        var totalPnL  = closed.Sum(r => r.GrossPnL ?? 0m);
        var totalWins = closed.Count(r => r.Outcome == "Win");
        var totalLoss = closed.Count(r => r.Outcome == "Loss");
        var winRate   = closed.Count > 0 ? totalWins * 100m / closed.Count : 0m;

        var blockedCount = records.Count(r => r.WasPositionBlocked);

        return
            $"🔬 *Shadow Trade Analysis*\n\n" +
            $"Total variants: {records.Count} ({open} open, {closed.Count} closed)\n" +
            $"Position-blocked evals: {blockedCount}\n" +
            $"Overall win rate: {winRate:F1}% ({totalWins}W/{totalLoss}L)\n" +
            $"Total gross P&L: ₹{totalPnL:F0}\n\n" +
            $"📊 *By Strategy:*\n{stratLines}\n\n" +
            $"📊 *Best ADR Multiplier:* {bestAdr.Mult}x ({bestAdr.Strategy})\n" +
            $"   Avg P&L ₹{bestAdr.AvgPnL:F0} | Win rate {bestAdr.WinRate:F0}% over {bestAdr.Count} trades\n\n" +
            $"📊 *Best Spread Width:* {bestWidth.Width}pts ({bestWidth.Strategy})\n" +
            $"   Avg P&L ₹{bestWidth.AvgPnL:F0} | Win rate {bestWidth.WinRate:F0}% over {bestWidth.Count} trades\n\n" +
            $"📊 *Best VIX Regime:* {bestVix.Regime} ({bestVix.Strategy})\n" +
            $"   Win rate {bestVix.WinRate:F1}% over {bestVix.Count} trades\n\n" +
            $"`GET /api/analytics/shadow-export`";
    }

    // ── CSV structure ─────────────────────────────────────────────────────────

    private static string CsvHeader() =>
        "Id,RealSignalGroupId,WasRealTrade,WasPositionBlocked," +
        "StrategyName,EntryVariation," +
        "EvaluatedAt," +
        "SpotAtEntry,VixAtEntry,VixRegime,Ema20AtEntry," +
        "AdrAtEntry,AtrAtEntry,AtrAverageAtEntry,GapPercent," +
        "DaysToExpiry,ExpiryDate," +
        "AdrMultiplierUsed,SpreadWidth,ProfitTargetPct,StopLossMultiplier," +
        "ShortStrike,LongStrike,PremiumCollected," +
        "ProfitTargetRs,StopLossThresholdRs," +
        "ExitReason,ExitDate,HoldingMinutes," +
        "PremiumAtExit,GrossPnL," + 
        "Outcome,LotSize,FeesRs,NetPnlRs";

    private static string ToCsvRow(ShadowTrade r)
    {
        var ist     = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        var istTime = TimeZoneInfo.ConvertTimeFromUtc(r.EvaluatedAt, ist)
                          .ToString("yyyy-MM-dd HH:mm");

        return string.Join(",",
            Q(r.Id.ToString()),
            Q(r.RealSignalGroupId?.ToString() ?? ""),
            r.WasRealTrade        ? "TRUE" : "FALSE",
            r.WasPositionBlocked  ? "TRUE" : "FALSE",
            Q(r.StrategyName),
            Q(r.EntryVariation),
            Q(istTime),
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
            N(r.AdrMultiplierUsed),
            r.SpreadWidth.ToString(),
            N(r.ProfitTargetPct),
            N(r.StopLossMultiplier),
            N(r.ShortStrike),
            N(r.LongStrike),
            N(r.PremiumCollected),
            N(r.ProfitTargetRs),
            N(r.StopLossThresholdRs),
            Q(r.ExitReason ?? ""),
            Q(r.ExitDate?.ToString("yyyy-MM-dd") ?? ""),
            N(r.HoldingMinutes),
            N(r.PremiumAtExit),
            N(r.GrossPnL),
            Q(r.Outcome),
            r.LotSize.ToString(),
            r.FeesRs?.ToString("F2") ?? "0.00",
            r.NetPnlRs?.ToString("F2") ?? "0.00");
    }

    private static string Q(string v) => $"\"{v.Replace("\"", "\"\"")}\"";
    private static string N(decimal? v) => v.HasValue
        ? v.Value.ToString("F2", CultureInfo.InvariantCulture) : "";
    private static string N(int? v)     => v.HasValue ? v.Value.ToString() : "";
    private static string N(decimal v)  => v.ToString("F2", CultureInfo.InvariantCulture);
}