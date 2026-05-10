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
    /// </summary>
    [HttpGet("export/download")]
    public async Task<IActionResult> Download(CancellationToken ct)
    {
        var path = await _csvExport.ExportClosedTradesAsync(ct);

        if (string.IsNullOrEmpty(path))
            return Ok(new { message = "No closed trades to export yet." });

        var bytes = await System.IO.File.ReadAllBytesAsync(path, ct);
        var fileName = Path.GetFileName(path);

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
        return File(bytes, "text/csv", fileName);
    }
}
