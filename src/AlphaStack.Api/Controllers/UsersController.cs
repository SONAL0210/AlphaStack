using MediatR;
using Microsoft.AspNetCore.Mvc;
using AlphaStack.Application.Features.Users;
using AlphaStack.Domain.Enums;

namespace AlphaStack.API.Controllers;

[ApiController]
[Route("api/users")]
public class UsersController : ControllerBase
{
    private readonly IMediator _mediator;

    public UsersController(IMediator mediator) => _mediator = mediator;

    /// <summary>
    /// Onboard a new user. Encrypts and stores their API keys.
    /// POST /api/users
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(CreateUserResponse), 201)]
    public async Task<IActionResult> CreateUser(
        [FromBody] CreateUserRequest request,
        CancellationToken ct)
    {
        var id = await _mediator.Send(new CreateUserProfileCommand(
            request.Username, request.Email,
            request.KiteApiKey, request.KiteApiSecret,
            request.TelegramBotToken, request.TelegramChatId,
            request.TotalCapitalAllocated,
            request.MaxDrawdownPercent,
            request.MaxCapitalPerTradePercent
        ), ct);

        return CreatedAtAction(nameof(GetUser), new { userId = id }, new CreateUserResponse(id));
    }

    /// <summary>
    /// Get a user profile (no sensitive fields returned).
    /// GET /api/users/{userId}
    /// </summary>
    [HttpGet("{userId:guid}")]
    [ProducesResponseType(typeof(UserProfileDto), 200)]
    public async Task<IActionResult> GetUser(Guid userId, CancellationToken ct)
    {
        var result = await _mediator.Send(new GetUserProfileQuery(userId), ct);
        return Ok(result);
    }

    /// <summary>
    /// Re-save Telegram credentials for an existing user.
    /// Useful when Data Protection keys changed and old encrypted values cannot be decrypted.
    /// PUT /api/users/{userId}/telegram
    /// </summary>
    [HttpPut("{userId:guid}/telegram")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> UpdateTelegramCredentials(
        Guid userId,
        [FromBody] UpdateTelegramCredentialsRequest request,
        CancellationToken ct)
    {
        await _mediator.Send(new UpdateTelegramCredentialsCommand(
            userId,
            request.TelegramBotToken,
            request.TelegramChatId), ct);

        return NoContent();
    }

    /// <summary>
    /// Enroll a user in a strategy (paper or live).
    /// POST /api/users/{userId}/enroll
    /// </summary>
    [HttpPost("{userId:guid}/enroll")]
    [ProducesResponseType(typeof(EnrollResponse), 201)]
    public async Task<IActionResult> EnrollInStrategy(
        Guid userId,
        [FromBody] EnrollRequest request,
        CancellationToken ct)
    {
        var executionId = await _mediator.Send(new EnrollUserInStrategyCommand(
            userId,
            request.StrategyDefinitionId,
            request.Mode,
            request.AllocatedCapital
        ), ct);

        return CreatedAtAction(nameof(GetUser), new { userId },
            new EnrollResponse(executionId));
    }
}

// ─── Request / Response DTOs ──────────────────────────────────────────────────

// Change CreateUserRequest record to:
public record CreateUserRequest(
    string Username,
    string Email,
    string? KiteApiKey,              // ← nullable, optional
    string? KiteApiSecret,           // ← nullable, optional
    string TelegramBotToken,
    long TelegramChatId,
    decimal TotalCapitalAllocated,
    decimal MaxDrawdownPercent,
    decimal MaxCapitalPerTradePercent);

public record CreateUserResponse(Guid UserId);

public record UpdateTelegramCredentialsRequest(
    string TelegramBotToken,
    long TelegramChatId);

public record EnrollRequest(
    Guid StrategyDefinitionId,
    ExecutionMode Mode,
    decimal AllocatedCapital);

public record EnrollResponse(Guid ExecutionId);
