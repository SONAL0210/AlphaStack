using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using AlphaStack.Application.Features.Trading;
using AlphaStack.Domain.Entities;
using AlphaStack.Domain.Enums;
using AlphaStack.Application.Common.Interfaces;

public class RiskManagerTests
{
    private readonly ITradeRepository _tradeRepo = Substitute.For<ITradeRepository>();
    private readonly IStrategyExecutionRepository _executionRepo = Substitute.For<IStrategyExecutionRepository>();
    private readonly RiskManager _riskManager;

    public RiskManagerTests()
    {
        _riskManager = new RiskManager(
            _tradeRepo,
            _executionRepo,
            NullLogger<RiskManager>.Instance);
    }

    private static UserProfile CreateUser(
        decimal totalCapital = 100000,
        decimal maxDrawdownPercent = 10,
        decimal maxCapitalPerTradePercent = 5) =>
        UserProfile.Create(
            username: "test",
            email: "test@example.com",
            encryptedKiteApiKey: null,
            encryptedKiteApiSecret: null,
            encryptedTelegramBotToken: "telegram-token",
            telegramChatId: 123,
            totalCapitalAllocated: totalCapital,
            maxDrawdownPercent: maxDrawdownPercent,
            maxCapitalPerTradePercent: maxCapitalPerTradePercent);

    private static StrategyExecution CreateExecution(
        Guid userProfileId,
        string strategyType,
        ExecutionMode mode = ExecutionMode.Paper,
        decimal allocatedCapital = 100000)
    {
        var definition = StrategyDefinition.Create(
            name: strategyType,
            description: "test",
            strategyType: strategyType,
            marketRegime: "Any");

        var execution = StrategyExecution.Create(
            userProfileId: userProfileId,
            strategyDefinitionId: definition.Id,
            mode: mode,
            allocatedCapital: allocatedCapital);

        // StrategyDefinition is a private-set navigation property with no public
        // setter on StrategyExecution — set via reflection since EF would normally
        // populate this on load, and we need it for Rule 4's strategyType check.
        typeof(StrategyExecution)
            .GetProperty(nameof(StrategyExecution.StrategyDefinition))!
            .SetValue(execution, definition);

        return execution;
    }

    [Fact]
    public async Task Reject_WhenCapitalExceedsLimit()
    {
        var user = CreateUser();
        var execution = CreateExecution(Guid.NewGuid(), "BullPutSpread");

        _tradeRepo.GetOpenByExecutionAsync(execution.Id, Arg.Any<CancellationToken>())
            .Returns(new List<Trade>());
        _executionRepo.GetByUserAsync(execution.UserProfileId, Arg.Any<CancellationToken>())
            .Returns(new List<StrategyExecution> { execution });

        var result = await _riskManager.ValidateEntryAsync(execution, user, 6000);

        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task Allow_WhenWithinAllLimits()
    {
        var user = CreateUser();
        var execution = CreateExecution(Guid.NewGuid(), "BullPutSpread");

        _tradeRepo.GetOpenByExecutionAsync(execution.Id, Arg.Any<CancellationToken>())
            .Returns(new List<Trade>());
        _executionRepo.GetByUserAsync(execution.UserProfileId, Arg.Any<CancellationToken>())
            .Returns(new List<StrategyExecution> { execution });

        var result = await _riskManager.ValidateEntryAsync(execution, user, 4000);

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task Reject_WhenCombinedDrawdownAcrossExecutionsBreached()
    {
        var userId = Guid.NewGuid();
        var user = CreateUser(totalCapital: 100000, maxDrawdownPercent: 10); // maxLoss = 10000

        var executionA = CreateExecution(userId, "BullPutSpread");
        var executionB = CreateExecution(userId, "BearCallSpread");

        executionA.RecordFilledTrade(-6000);
        executionB.RecordFilledTrade(-5000);

        _tradeRepo.GetOpenByExecutionAsync(executionA.Id, Arg.Any<CancellationToken>())
            .Returns(new List<Trade>());
        _executionRepo.GetByUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<StrategyExecution> { executionA, executionB });

        var result = await _riskManager.ValidateEntryAsync(executionA, user, 4000);

        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task Allow_WhenOtherExecutionModeDoesNotCountTowardDrawdown()
    {
        var userId = Guid.NewGuid();
        var user = CreateUser(totalCapital: 100000, maxDrawdownPercent: 10);

        var executionA = CreateExecution(userId, "BullPutSpread", mode: ExecutionMode.Paper);
        var executionB = CreateExecution(userId, "BullPutSpread", mode: ExecutionMode.Live);
        executionB.RecordFilledTrade(-50000);

        _tradeRepo.GetOpenByExecutionAsync(executionA.Id, Arg.Any<CancellationToken>())
            .Returns(new List<Trade>());
        _executionRepo.GetByUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<StrategyExecution> { executionA, executionB });

        var result = await _riskManager.ValidateEntryAsync(executionA, user, 4000);

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task Reject_IronCondorEntry_WhenBothBullPutAndBearCallOpen()
    {
        var userId = Guid.NewGuid();
        var user = CreateUser();

        var icExecution = CreateExecution(userId, "NiftyIronCondor");
        var bpsExecution = CreateExecution(userId, "BullPutSpread");
        var bcsExecution = CreateExecution(userId, "BearCallSpread");

        _tradeRepo.GetOpenByExecutionAsync(icExecution.Id, Arg.Any<CancellationToken>())
            .Returns(new List<Trade>());
        _tradeRepo.GetOpenByExecutionAsync(bpsExecution.Id, Arg.Any<CancellationToken>())
            .Returns(new List<Trade> { CreateOpenTrade(bpsExecution.Id) });
        _tradeRepo.GetOpenByExecutionAsync(bcsExecution.Id, Arg.Any<CancellationToken>())
            .Returns(new List<Trade> { CreateOpenTrade(bcsExecution.Id) });

        _executionRepo.GetByUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<StrategyExecution> { icExecution, bpsExecution, bcsExecution });

        var result = await _riskManager.ValidateEntryAsync(icExecution, user, 4000);

        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task Allow_IronCondorEntry_WhenOnlyBullPutOpen()
    {
        var userId = Guid.NewGuid();
        var user = CreateUser();

        var icExecution = CreateExecution(userId, "NiftyIronCondor");
        var bpsExecution = CreateExecution(userId, "BullPutSpread");
        var bcsExecution = CreateExecution(userId, "BearCallSpread");

        _tradeRepo.GetOpenByExecutionAsync(icExecution.Id, Arg.Any<CancellationToken>())
            .Returns(new List<Trade>());
        _tradeRepo.GetOpenByExecutionAsync(bpsExecution.Id, Arg.Any<CancellationToken>())
            .Returns(new List<Trade> { CreateOpenTrade(bpsExecution.Id) });
        _tradeRepo.GetOpenByExecutionAsync(bcsExecution.Id, Arg.Any<CancellationToken>())
            .Returns(new List<Trade>()); // BCS has none open

        _executionRepo.GetByUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<StrategyExecution> { icExecution, bpsExecution, bcsExecution });

        var result = await _riskManager.ValidateEntryAsync(icExecution, user, 4000);

        Assert.True(result.IsAllowed);
    }

    private static Trade CreateOpenTrade(Guid strategyExecutionId) =>
        Trade.Create(
            strategyExecutionId: strategyExecutionId,
            symbol: "NIFTY260819P24000",
            direction: TradeDirection.Short,
            quantity: 65,
            entrySignalGroupId: Guid.NewGuid(),
            entryClientOrderId: Guid.NewGuid().ToString("N"));
}