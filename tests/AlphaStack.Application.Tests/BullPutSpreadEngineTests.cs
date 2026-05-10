using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Domain.Entities;
using AlphaStack.Domain.Enums;
using AlphaStack.Infrastructure.ExternalServices.KiteConnect.Strategies;
using Xunit;

namespace AlphaStack.Application.Tests;

// ── Pure logic tests (no I/O) ─────────────────────────────────────────────────

public class BullPutSpreadEngineLogicTests
{
    [Theory]
    [InlineData(new double[] { 100, 102, 101, 103, 105, 104, 106, 108, 107, 109,
                               110, 112, 111, 113, 115, 114, 116, 118, 117, 119,
                               120 }, 20, 110.5)]
    public void CalculateEma_ReturnsExpectedValue(double[] closes, int period, double expectedApprox)
    {
        var result = BullPutSpreadEngine.CalculateEma(
            closes.Select(c => (decimal)c).ToList(), period);

        ((double)result).Should().BeApproximately(expectedApprox, precision: 1.0);
    }

    [Fact]
    public void CalculateEma_InsufficientData_Throws()
    {
        var act = () => BullPutSpreadEngine.CalculateEma(
            new List<decimal> { 100, 200 }, period: 20);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(24187, 24200)]   // rounds up to nearest 50
    [InlineData(24150, 24150)]   // already on boundary
    [InlineData(23901, 23900)]   // rounds down
    [InlineData(24075, 24100)]   // rounds up (midpoint rounds to even — .NET default)
    public void RoundToNiftyStrike_RoundsToNearest50(decimal input, decimal expected)
    {
        BullPutSpreadEngine.RoundToNiftyStrike(input).Should().Be(expected);
    }

    [Fact]
    public void NearestTuesdayExpiry_WhenTodayIsTuesday_ReturnsNextTuesday()
    {
        var tuesday = new DateTime(2026, 5, 5, 9, 20, 0);
        var result = BullPutSpreadEngine.NearestTuesdayExpiry(tuesday);
        result.Should().Be(new DateOnly(2026, 5, 12));
    }

    [Fact]
    public void NearestTuesdayExpiry_WhenTodayIsMonday_ReturnsThisTuesday()
    {
        var monday = new DateTime(2026, 5, 4, 9, 20, 0);
        var result = BullPutSpreadEngine.NearestTuesdayExpiry(monday);
        result.Should().Be(new DateOnly(2026, 5, 5));
    }

    [Fact]
    public void NearestTuesdayExpiry_WhenTodayIsWednesday_ReturnsNextTuesday()
    {
        var wednesday = new DateTime(2026, 5, 6, 9, 20, 0);
        var result = BullPutSpreadEngine.NearestTuesdayExpiry(wednesday);
        result.Should().Be(new DateOnly(2026, 5, 12));
    }

    [Theory]
    [InlineData(2026, 5, 5, 14, 44, false)]
    [InlineData(2026, 5, 5, 14, 45, true)]
    [InlineData(2026, 5, 5, 15, 0, true)]
    public void ShouldExitForExpiry_OnExpiryDay_UsesFullTimeComparison(
        int year, int month, int day, int hour, int minute, bool expected)
    {
        var now = new DateTime(year, month, day, hour, minute, 0);
        var result = BullPutSpreadEngine.ShouldExitForExpiry(new DateOnly(2026, 5, 5), now);
        result.Should().Be(expected);
    }

    [Fact]
    public void ShouldExitForExpiry_WhenExpiryIsPast_ReturnsTrue()
    {
        var now = new DateTime(2026, 5, 6, 9, 20, 0);
        var result = BullPutSpreadEngine.ShouldExitForExpiry(new DateOnly(2026, 5, 5), now);
        result.Should().BeTrue();
    }
}

// ── Engine evaluation tests (mocked dependencies) ─────────────────────────────

public class BullPutSpreadEngineEvaluationTests
{
    private readonly IMarketDataProvider _marketData = Substitute.For<IMarketDataProvider>();
    private readonly IInstrumentRepository _instruments = Substitute.For<IInstrumentRepository>();
    private readonly IPositionRepository _positions = Substitute.For<IPositionRepository>();
    private readonly BullPutSpreadEngine _engine;

    private static readonly StrategyExecution TestExecution = StrategyExecution.Create(
        userProfileId: Guid.NewGuid(),
        strategyDefinitionId: Guid.NewGuid(),
        mode: ExecutionMode.Paper,
        allocatedCapital: 300000m);

    public BullPutSpreadEngineEvaluationTests()
    {
        _engine = new BullPutSpreadEngine(
            _marketData, _instruments, _positions,
            NullLogger<BullPutSpreadEngine>.Instance);
    }

    [Fact]
    public async Task EvaluateAsync_WhenVixAboveThreshold_ReturnsNull()
    {
        _positions.GetOpenByExecutionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<Position>());

        _marketData.GetQuoteAsync("INDIA VIX", "NSE", Arg.Any<CancellationToken>())
            .Returns(MakeQuote("INDIA VIX", lastPrice: 18m));  // above 15 threshold

        var result = await _engine.EvaluateAsync(TestExecution);

        result.Should().BeNull();
    }

    [Fact]
    public async Task EvaluateAsync_WhenOpenPositionsExist_ReturnsNull()
    {
        _positions.GetOpenByExecutionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<Position> { MakeOpenPosition() });

        var result = await _engine.EvaluateAsync(TestExecution);

        result.Should().BeNull();
        // Should not even call market data if there's already an open position
        await _marketData.DidNotReceive().GetQuoteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateAsync_WhenSpotBelowEma_ReturnsNull()
    {
        _positions.GetOpenByExecutionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<Position>());

        _marketData.GetQuoteAsync("INDIA VIX", "NSE", Arg.Any<CancellationToken>())
            .Returns(MakeQuote("INDIA VIX", lastPrice: 12m)); // VIX ok

        _marketData.GetQuoteAsync("NIFTY 50", "NSE", Arg.Any<CancellationToken>())
            .Returns(MakeQuote("NIFTY 50", lastPrice: 23000m)); // spot below EMA

        // Return candles where EMA20 would be ~24000 (above spot)
        _marketData.GetHistoricalDataAsync(
                Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(GenerateCandles(basePrice: 24000m, count: 30));

        var result = await _engine.EvaluateAsync(TestExecution);

        result.Should().BeNull();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Quote MakeQuote(string symbol, decimal lastPrice) =>
        new(symbol, "NSE", lastPrice, lastPrice - 1, lastPrice + 1,
            lastPrice, lastPrice, lastPrice, lastPrice - 5, 1000, 0, DateTime.UtcNow);

    private static Position MakeOpenPosition() => Position.Open(
        Guid.NewGuid(), ExecutionMode.Paper, Guid.NewGuid(),
        "NIFTY24DEC24000PE", "NFO", 12345,
        OrderSide.Sell, 50, 100m,
        OptionType.Put, 24000m, new DateOnly(2024, 12, 26));

    private static List<Candle> GenerateCandles(decimal basePrice, int count) =>
        Enumerable.Range(0, count)
            .Select(i => new Candle(
                DateTime.UtcNow.AddDays(-count + i),
                basePrice, basePrice + 50, basePrice - 50, basePrice, 1_000_000))
            .ToList();
}
