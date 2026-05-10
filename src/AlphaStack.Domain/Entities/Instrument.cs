using AlphaStack.Domain.Common;
using AlphaStack.Domain.Enums;

namespace AlphaStack.Domain.Entities;

/// <summary>
/// Cached copy of the Kite instrument master (refreshed daily).
/// Used for token lookup without hitting Kite API on every signal.
/// </summary>
public class Instrument : BaseEntity
{
    public int InstrumentToken { get; private set; }
    public string TradingSymbol { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public string Exchange { get; private set; } = default!;
    public InstrumentType InstrumentType { get; private set; }

    // Options/futures specific
    public OptionType? OptionType { get; private set; }
    public decimal? StrikePrice { get; private set; }
    public DateOnly? ExpiryDate { get; private set; }

    public decimal LotSize { get; private set; }
    public decimal TickSize { get; private set; }

    public DateTime LastSyncedAt { get; private set; }

    private Instrument() { }

    public static Instrument Create(
        int instrumentToken,
        string tradingSymbol,
        string name,
        string exchange,
        InstrumentType instrumentType,
        decimal lotSize,
        decimal tickSize,
        OptionType? optionType = null,
        decimal? strikePrice = null,
        DateOnly? expiryDate = null)
    {
        return new Instrument
        {
            InstrumentToken = instrumentToken,
            TradingSymbol = tradingSymbol,
            Name = name,
            Exchange = exchange,
            InstrumentType = instrumentType,
            LotSize = lotSize,
            TickSize = tickSize,
            OptionType = optionType,
            StrikePrice = strikePrice,
            ExpiryDate = expiryDate,
            LastSyncedAt = DateTime.UtcNow
        };
    }
}
