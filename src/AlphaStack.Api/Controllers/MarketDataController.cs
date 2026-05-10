using MediatR;
using Microsoft.AspNetCore.Mvc;
using AlphaStack.Application.Features.MarketData;

namespace AlphaStack.API.Controllers;

[ApiController]
[Route("api/market-data")]
public class MarketDataController : ControllerBase
{
    private readonly IMediator _mediator;

    public MarketDataController(IMediator mediator) => _mediator = mediator;

    /// <summary>
    /// Get a live quote for a symbol.
    /// GET /api/market-data/quote?userId={guid}&symbol=NIFTY&exchange=NSE
    /// </summary>
    [HttpGet("quote")]
    [ProducesResponseType(typeof(QuoteDto), 200)]
    public async Task<IActionResult> GetQuote(
        [FromQuery] Guid userId,
        [FromQuery] string symbol,
        [FromQuery] string exchange,
        CancellationToken ct)
    {
        var result = await _mediator.Send(new GetQuoteQuery(userId, symbol, exchange), ct);
        return Ok(result);
    }

    /// <summary>
    /// Get OHLCV historical candles.
    /// GET /api/market-data/historical?userId=...&token=...&interval=day&from=2024-01-01&to=2024-12-31
    /// </summary>
    [HttpGet("historical")]
    [ProducesResponseType(typeof(IReadOnlyList<CandleDto>), 200)]
    public async Task<IActionResult> GetHistorical(
        [FromQuery] Guid userId,
        [FromQuery] int token,
        [FromQuery] string interval,
        [FromQuery] DateTime from,
        [FromQuery] DateTime to,
        CancellationToken ct)
    {
        var result = await _mediator.Send(
            new GetHistoricalDataQuery(userId, token, interval, from, to), ct);
        return Ok(result);
    }

    /// <summary>
    /// Sync the Kite instrument master for an exchange into local DB.
    /// POST /api/market-data/instruments/sync
    /// </summary>
    [HttpPost("instruments/sync")]
    [ProducesResponseType(typeof(SyncInstrumentsResult), 200)]
    public async Task<IActionResult> SyncInstruments(
        [FromQuery] Guid userId,
        [FromQuery] string exchange,
        CancellationToken ct)
    {
        var result = await _mediator.Send(new SyncInstrumentsCommand(userId, exchange), ct);
        return Ok(result);
    }

    /// <summary>
    /// Look up a cached instrument by symbol and exchange.
    /// GET /api/market-data/instruments/lookup?symbol=NIFTY24DEC24000CE&exchange=NFO
    /// </summary>
    [HttpGet("instruments/lookup")]
    [ProducesResponseType(typeof(InstrumentDto), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> LookupInstrument(
        [FromQuery] string symbol,
        [FromQuery] string exchange,
        CancellationToken ct)
    {
        var result = await _mediator.Send(new LookupInstrumentQuery(symbol, exchange), ct);
        return result is null ? NotFound() : Ok(result);
    }
}
