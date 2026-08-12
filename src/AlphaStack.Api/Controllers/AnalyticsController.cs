using Microsoft.AspNetCore.Mvc;
using AlphaStack.Application.Features.Analytics;


namespace AlphaStack.API.Controllers;

/// <summary>
/// Analytics and research data endpoints.
///
/// GET /api/analytics/export         — export all closed trades to CSV
/// GET /api/analytics/export/all     — export all trades including open positions
/// </summary>
[ApiController]
[Route("api/analytics")]
public class AnalyticsController : ControllerBase
{
    private readonly CsvExportService _csvExport;
    private readonly ILogger<AnalyticsController> _logger;
    private readonly ShadowCsvExportService _shadowCsvExport;

    public AnalyticsController(
        CsvExportService csvExport,
        ILogger<AnalyticsController> logger,
        ShadowCsvExportService shadowCsvExport)
    {
        _csvExport = csvExport;
        _logger = logger;
        _shadowCsvExport = shadowCsvExport;
    }

    /// <summary>
    /// Export all CLOSED trades to CSV.
    /// Returns the file path on disk where the CSV was written.
    /// Kept as a disk-writing endpoint deliberately — meant for manual `scp`
    /// retrieval, not a browser download, so the file persisting briefly is
    /// intentional here (unlike Download()/ExportShadowTrades() below).
    /// </summary>
    [HttpGet("export")]
    public async Task<IActionResult> ExportClosed(CancellationToken ct)
    {
        _logger.LogInformation("[Analytics] CSV export requested (closed trades)");

        var path = await _csvExport.ExportClosedTradesAsync(ct);

        if (string.IsNullOrEmpty(path))
            return Ok(new { message = "No closed trades to export yet." });

        return Ok(new
        {
            message = "Export complete.",
            file_path = path,
            hint = $"Copy with: cp \"{path}\" ~/Downloads/trades.csv"
        });
    }

    /// <summary>
    /// Export ALL trades (open + closed) — useful for mid-session snapshots.
    /// Same manual-scp intent as ExportClosed() — disk persistence intentional.
    /// NOTE: each call creates a new timestamped file (trade_analytics_all_*.csv)
    /// that is NOT cleaned up automatically — these will accumulate over repeated
    /// calls. Low volume/frequency expected, but worth a periodic manual check
    /// of ~/alphastack/exports/ if this endpoint gets used often.
    /// </summary>
    [HttpGet("export/all")]
    public async Task<IActionResult> ExportAll(CancellationToken ct)
    {
        _logger.LogInformation("[Analytics] CSV export requested (all trades)");

        var path = await _csvExport.ExportAllTradesAsync(ct);

        if (string.IsNullOrEmpty(path))
            return Ok(new { message = "No trades to export yet." });

        return Ok(new
        {
            message = "Export complete.",
            file_path = path,
            hint = $"Copy with: cp \"{path}\" ~/Downloads/trades_all.csv"
        });
    }

    /// <summary>
    /// Download the CSV directly as a file attachment (opens save dialog in browser).
    /// File is written to disk only as an intermediate step to produce the bytes
    /// for this response — deleted immediately after reading, since nothing else
    /// needs it once the response has the content. This is the endpoint your
    /// actual usage pattern goes through (per server logs), so this is the fix
    /// for "CSV files accumulating on server."
    /// </summary>
    [HttpGet("export/download")]
    public async Task<IActionResult> Download(CancellationToken ct)
    {
        var path = await _csvExport.ExportClosedTradesAsync(ct);

        if (string.IsNullOrEmpty(path))
            return Ok(new { message = "No closed trades to export yet." });

        var bytes = await System.IO.File.ReadAllBytesAsync(path, ct);
        var fileName = Path.GetFileName(path);

        TryDeleteExportFile(path);

        return File(bytes, "text/csv", fileName);
    }

    [HttpGet("shadow-export")]
    public async Task<IActionResult> ExportShadowTrades(CancellationToken ct)
    {
        var path = await _shadowCsvExport.ExportAllAsync(ct);

        if (string.IsNullOrEmpty(path))
            return Ok("No shadow trades to export yet.");

        var bytes    = await System.IO.File.ReadAllBytesAsync(path, ct);
        var fileName = Path.GetFileName(path);

        TryDeleteExportFile(path);

        return File(bytes, "text/csv", fileName);
    }

    /// <summary>
    /// Best-effort cleanup of the temp export file after its bytes have already
    /// been read into the response. Failure to delete (e.g. file lock, permission
    /// issue) is logged but never allowed to fail the actual download — the user
    /// still gets their file either way; at worst a stray file is left behind for
    /// the existing cron cleanup to catch later.
    /// </summary>
    private void TryDeleteExportFile(string path)
    {
        try
        {
            System.IO.File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Analytics] Failed to delete temp export file {Path}", path);
        }
    }
}