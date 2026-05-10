using MediatR;
using AlphaStack.Application.Common.Interfaces;

namespace AlphaStack.Application.Features.MarketData;

// ─── Get Quote ────────────────────────────────────────────────────────────────

public record GetQuoteQuery(Guid UserProfileId, string TradingSymbol, string Exchange)
    : IRequest<QuoteDto>;

public record QuoteDto(
    string TradingSymbol,
    string Exchange,
    decimal LastPrice,
    decimal BidPrice,
    decimal AskPrice,
    decimal OpenPrice,
    decimal HighPrice,
    decimal LowPrice,
    decimal ClosePrice,
    decimal OpenInterest,
    long Volume,
    decimal ChangePercent);

public class GetQuoteHandler : IRequestHandler<GetQuoteQuery, QuoteDto>
{
    private readonly IMarketDataProvider _marketData;

    public GetQuoteHandler(IMarketDataProvider marketData) => _marketData = marketData;

    public async Task<QuoteDto> Handle(GetQuoteQuery request, CancellationToken ct)
    {
        var q = await _marketData.GetQuoteAsync(request.TradingSymbol, request.Exchange, ct)
            ?? throw new InvalidOperationException(
                $"No quote returned for {request.Exchange}:{request.TradingSymbol}");

        var change = q.ClosePrice == 0 ? 0
            : Math.Round((q.LastPrice - q.ClosePrice) / q.ClosePrice * 100, 2);

        return new QuoteDto(
            q.TradingSymbol, q.Exchange,
            q.LastPrice, q.BidPrice, q.AskPrice,
            q.OpenPrice, q.HighPrice, q.LowPrice, q.ClosePrice,
            q.OpenInterest, q.Volume, change);
    }
}

// ─── Get Historical OHLCV ─────────────────────────────────────────────────────

public record GetHistoricalDataQuery(
    Guid UserProfileId,
    int InstrumentToken,
    string Interval,
    DateTime From,
    DateTime To) : IRequest<IReadOnlyList<CandleDto>>;

public record CandleDto(DateTime Timestamp, decimal Open, decimal High, decimal Low, decimal Close, long Volume);

public class GetHistoricalDataHandler : IRequestHandler<GetHistoricalDataQuery, IReadOnlyList<CandleDto>>
{
    private readonly IMarketDataProvider _marketData;

    public GetHistoricalDataHandler(IMarketDataProvider marketData) => _marketData = marketData;

    public async Task<IReadOnlyList<CandleDto>> Handle(GetHistoricalDataQuery request, CancellationToken ct)
    {
        var candles = await _marketData.GetHistoricalDataAsync(
            request.InstrumentToken,
            request.Interval,
            request.From,
            request.To,
            ct);

        return candles
            .Select(c => new CandleDto(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume))
            .ToList();
    }
}

// ─── Sync Instrument Master ───────────────────────────────────────────────────

public record SyncInstrumentsCommand(Guid UserProfileId, string Exchange) : IRequest<SyncInstrumentsResult>;
public record SyncInstrumentsResult(int SyncedCount, DateTime SyncedAt);

public class SyncInstrumentsHandler : IRequestHandler<SyncInstrumentsCommand, SyncInstrumentsResult>
{
    private readonly IKiteMarketDataService _kiteData;
    private readonly IInstrumentRepository _instruments;
    private readonly IUnitOfWork _uow;

    public SyncInstrumentsHandler(
        IKiteMarketDataService kiteData,
        IInstrumentRepository instruments,
        IUnitOfWork uow)
    {
        _kiteData = kiteData;
        _instruments = instruments;
        _uow = uow;
    }

    public async Task<SyncInstrumentsResult> Handle(SyncInstrumentsCommand request, CancellationToken ct)
    {
        var instruments = await _kiteData.GetInstrumentsAsync(request.Exchange, ct);
        await _instruments.BulkUpsertAsync(instruments, ct);
        await _uow.SaveChangesAsync(ct);

        return new SyncInstrumentsResult(instruments.Count, DateTime.UtcNow);
    }
}

// ─── Lookup Instrument by Symbol ──────────────────────────────────────────────

public record LookupInstrumentQuery(string TradingSymbol, string Exchange) : IRequest<InstrumentDto?>;

public record InstrumentDto(
    int InstrumentToken,
    string TradingSymbol,
    string Name,
    string Exchange,
    string InstrumentType,
    decimal LotSize,
    decimal TickSize,
    string? OptionType,
    decimal? StrikePrice,
    DateOnly? ExpiryDate);

public class LookupInstrumentHandler : IRequestHandler<LookupInstrumentQuery, InstrumentDto?>
{
    private readonly IInstrumentRepository _instruments;

    public LookupInstrumentHandler(IInstrumentRepository instruments) => _instruments = instruments;

    public async Task<InstrumentDto?> Handle(LookupInstrumentQuery request, CancellationToken ct)
    {
        var i = await _instruments.GetBySymbolAndExchangeAsync(request.TradingSymbol, request.Exchange, ct);
        if (i is null) return null;

        return new InstrumentDto(
            i.InstrumentToken, i.TradingSymbol, i.Name, i.Exchange,
            i.InstrumentType.ToString(), i.LotSize, i.TickSize,
            i.OptionType?.ToString(), i.StrikePrice, i.ExpiryDate);
    }
}
