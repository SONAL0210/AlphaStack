using Xunit;
using AlphaStack.Application.Features.Trading;
using AlphaStack.Domain.Entities;
using AlphaStack.Domain.Enums;

public class FyersOrderMapperTests
{
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

    private static SignalLeg CreateLeg(OrderSide side, int qty = 65) =>
        new("NIFTY260819P24000", "NFO", 0, side, qty, 50m, OptionType.Put, 24000m, null);

    [Fact]
    public void ToMultilegRequest_TwoLegs_ProducesOrderType2L()
    {
        var signal = CreateSignal(CreateLeg(OrderSide.Sell), CreateLeg(OrderSide.Buy));

        var request = FyersOrderMapper.ToMultilegRequest(
            signal,
            fyersSymbolsInLegOrder: ["NSE:NIFTY26AUG1924000PE", "NSE:NIFTY26AUG1923800PE"],
            limitPricesInLegOrder: [50m, 25m]);

        Assert.Equal("2L", request.OrderType);
        Assert.Equal(2, request.Legs.Count);
    }

    [Fact]
    public void ToMultilegRequest_OrderTagHasNoHyphens()
    {
        var signal = CreateSignal(CreateLeg(OrderSide.Sell), CreateLeg(OrderSide.Buy));

        var request = FyersOrderMapper.ToMultilegRequest(
            signal, ["A", "B"], [50m, 25m]);

        Assert.DoesNotContain("-", request.OrderTag);
    }

    [Fact]
    public void ToMultilegRequest_SellSideMapsToNegativeOne()
    {
        var signal = CreateSignal(CreateLeg(OrderSide.Sell), CreateLeg(OrderSide.Buy));

        var request = FyersOrderMapper.ToMultilegRequest(
            signal, ["A", "B"], [50m, 25m]);

        Assert.Equal(-1, request.Legs[0].Side);
        Assert.Equal(1, request.Legs[1].Side);
    }

    [Fact]
    public void ToMultilegRequest_FourLegs_Throws()
    {
        var signal = CreateSignal(
            CreateLeg(OrderSide.Sell), CreateLeg(OrderSide.Buy),
            CreateLeg(OrderSide.Sell), CreateLeg(OrderSide.Buy));

        Assert.Throws<ArgumentException>(() =>
            FyersOrderMapper.ToMultilegRequest(signal, ["A", "B", "C", "D"], [1m, 1m, 1m, 1m]));
    }

    [Fact]
public void ToSequentialOrderRequests_BuyLegComesFirst()
{
    var signal = CreateSignal(CreateLeg(OrderSide.Sell), CreateLeg(OrderSide.Buy));

    var requests = FyersOrderMapper.ToSequentialOrderRequests(
        signal, ["SELL_SYM", "BUY_SYM"], [50m, 25m]);

    Assert.Equal(1, requests[0].Side);  // Buy first
    Assert.Equal(-1, requests[1].Side); // Sell second
}
}