using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using AlphaStack.Application.Features.Trading;
using AlphaStack.Domain.Entities;
using AlphaStack.Application.Common.Interfaces;

public class RiskManagerTests
{
    private readonly ITradeRepository _tradeRepo = Substitute.For<ITradeRepository>();
    private readonly RiskManager _riskManager;

    public RiskManagerTests()
    {
        _riskManager = new RiskManager(
            _tradeRepo,
            NullLogger<RiskManager>.Instance);
    }

    [Fact]
    public async Task Reject_WhenCapitalExceedsLimit()
    {
        var user = UserProfile.Create(
            username: "test",
            email: "test@example.com",
            encryptedKiteApiKey: null,
            encryptedKiteApiSecret: null,
            encryptedTelegramBotToken: "telegram-token",
            telegramChatId: 123,
            totalCapitalAllocated: 100000,
            maxDrawdownPercent: 10,
            maxCapitalPerTradePercent: 5);

        var execution = StrategyExecution.Create(
            userProfileId: Guid.NewGuid(),
            strategyDefinitionId: Guid.NewGuid(),
            mode: AlphaStack.Domain.Enums.ExecutionMode.Paper,
            allocatedCapital: 100000);

        var result = await _riskManager.ValidateEntryAsync(execution, user, 6000);

        Assert.False(result.IsAllowed);
    }
}
