using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using AlphaStack.Application.Features.Trading;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Domain.Entities;
using AlphaStack.Domain.Enums;

public class LiquidityGuardTests
{
    private readonly IMarketDataProvider _marketData = Substitute.For<IMarketDataProvider>();
    private readonly LiquidityGuard _guard;

    public LiquidityGuardTests()
    {
        var configValues = new Dictionary<string, string?>
        {
            ["LiveTrading:MaxSpreadPercent"] = "3.0"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        _guard = new LiquidityGuard(_marketData, configuration, NullLogger<LiquidityGuard>.Instance);
    }

    private static StrategySignal CreateSignal(params SignalLeg[] legs) =>
        new(
            SignalGroupId: Guid.NewGuid(),
            StrategyExecutionId: Guid.NewGuid(),
            StrategyType: "BullPutSpread",
            Action: SignalAction.Enter,
            Mode: ExecutionMode.Live,
            Legs: legs,
            Rationale: "test",
            GeneratedAt: DateTime.UtcNow);

    private static SignalLeg CreateLeg(string symbol = "NIFTY260819P24000") =>
        new(
            TradingSymbol: symbol,
            Exchange: "NFO",
            InstrumentToken: 0,
            Side: OrderSide.Sell,
            Quantity: 65,
            LastPrice: 50m,
            OptionType: OptionType.Put,
            StrikePrice: 24000m,
            ExpiryDate: null);

    [Fact]
    public async Task Allow_WhenSpreadWithinLimit()
    {
        var leg = CreateLeg();
        var signal = CreateSignal(leg);

        // bid=49.5, ask=50.5: spread = 1/50*100 = 2% — under the 3% limit
        _marketData.GetQuoteAsync(leg.TradingSymbol, leg.Exchange, Arg.Any<CancellationToken>())
            .Returns(new Quote(leg.TradingSymbol, leg.Exchange, 50m, 49.5m, 50.5m, 0, 0, 0, 0, 0, 0, DateTime.UtcNow));

        var result = await _guard.ValidateSignalLiquidityAsync(signal);

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task Reject_WhenSpreadExceedsLimit()
    {
        var leg = CreateLeg();
        var signal = CreateSignal(leg);

        // bid=45, ask=55: spread = 10/50*100 = 20% — well over the 3% limit
        _marketData.GetQuoteAsync(leg.TradingSymbol, leg.Exchange, Arg.Any<CancellationToken>())
            .Returns(new Quote(leg.TradingSymbol, leg.Exchange, 50m, 45m, 55m, 0, 0, 0, 0, 0, 0, DateTime.UtcNow));

        var result = await _guard.ValidateSignalLiquidityAsync(signal);

        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task Reject_WhenBidAskBothZero()
    {
        var leg = CreateLeg();
        var signal = CreateSignal(leg);

        _marketData.GetQuoteAsync(leg.TradingSymbol, leg.Exchange, Arg.Any<CancellationToken>())
            .Returns(new Quote(leg.TradingSymbol, leg.Exchange, 50m, 0m, 0m, 0, 0, 0, 0, 0, 0, DateTime.UtcNow));

        var result = await _guard.ValidateSignalLiquidityAsync(signal);

        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task Reject_WhenQuoteUnavailable()
    {
        var leg = CreateLeg();
        var signal = CreateSignal(leg);

        _marketData.GetQuoteAsync(leg.TradingSymbol, leg.Exchange, Arg.Any<CancellationToken>())
            .Returns((Quote?)null);

        var result = await _guard.ValidateSignalLiquidityAsync(signal);

        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task Reject_WhenAnyLegFailsEvenIfOthersPass()
    {
        var goodLeg = CreateLeg("NIFTY260819P24000");
        var badLeg = CreateLeg("NIFTY260819P23800");
        var signal = CreateSignal(goodLeg, badLeg);

        _marketData.GetQuoteAsync(goodLeg.TradingSymbol, goodLeg.Exchange, Arg.Any<CancellationToken>())
            .Returns(new Quote(goodLeg.TradingSymbol, goodLeg.Exchange, 50m, 49.5m, 50.5m, 0, 0, 0, 0, 0, 0, DateTime.UtcNow));
        _marketData.GetQuoteAsync(badLeg.TradingSymbol, badLeg.Exchange, Arg.Any<CancellationToken>())
            .Returns(new Quote(badLeg.TradingSymbol, badLeg.Exchange, 50m, 0m, 0m, 0, 0, 0, 0, 0, 0, DateTime.UtcNow));

        var result = await _guard.ValidateSignalLiquidityAsync(signal);

        Assert.False(result.IsAllowed);
    }
}