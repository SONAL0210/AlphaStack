using MediatR;
using Microsoft.AspNetCore.Mvc;
using AlphaStack.Application.Features.Auth;

namespace AlphaStack.API.Controllers;

[ApiController]
[Route("api/kite")]
public class KiteAuthController : ControllerBase
{
    private readonly IMediator _mediator;

    public KiteAuthController(IMediator mediator) => _mediator = mediator;

    /// <summary>
    /// Step 1: Get the Zerodha login URL for a user.
    /// Frontend redirects user to this URL to begin OAuth flow.
    /// GET /api/kite/login-url?userId={guid}
    /// </summary>
    [HttpGet("login-url")]
    [ProducesResponseType(typeof(LoginUrlResponse), 200)]
    public async Task<IActionResult> GetLoginUrl(
        [FromQuery] Guid userId,
        CancellationToken ct)
    {
        var url = await _mediator.Send(new GetKiteLoginUrlQuery(userId), ct);
        return Ok(new LoginUrlResponse(url));
    }

    /// <summary>
    /// Step 2: Zerodha OAuth callback. Called by Zerodha after user logs in.
    /// Zerodha redirects to: /api/kite/callback?request_token=xxx&action=login&status=success
    /// We exchange the request_token for an access_token and store it.
    /// GET /api/kite/callback
    /// </summary>
    [HttpGet("callback")]
    public async Task<IActionResult> OAuthCallback(
        [FromQuery(Name = "request_token")] string requestToken,
        [FromQuery] string status,
        [FromQuery] Guid userId,      // passed via state or pre-configured per user
        CancellationToken ct)
    {
        if (status != "success" || string.IsNullOrWhiteSpace(requestToken))
            return BadRequest(new { error = "Kite login failed or cancelled." });

        var result = await _mediator.Send(new CompleteKiteAuthCommand(userId, requestToken), ct);

        if (!result.Success)
            return StatusCode(500, new { error = "Failed to establish Kite session." });

        return Ok(new
        {
            message = "Kite session established successfully.",
            userId = result.UserId,
            expiresAt = result.ExpiresAt
        });
    }

    /// <summary>
    /// Check whether a user's Kite session is currently valid.
    /// GET /api/kite/session-status?userId={guid}
    /// </summary>
    [HttpGet("session-status")]
    [ProducesResponseType(typeof(SessionStatusResponse), 200)]
    public async Task<IActionResult> GetSessionStatus(
        [FromQuery] Guid userId,
        CancellationToken ct)
    {
        var status = await _mediator.Send(new GetKiteSessionStatusQuery(userId), ct);
        return Ok(new SessionStatusResponse(status.IsValid, status.ExpiresAt, status.Username));
    }
}

public record LoginUrlResponse(string LoginUrl);
public record SessionStatusResponse(bool IsValid, DateTime? ExpiresAt, string? Username);
