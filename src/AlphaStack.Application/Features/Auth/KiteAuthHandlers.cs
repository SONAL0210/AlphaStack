using MediatR;
using AlphaStack.Application.Common.Interfaces;

namespace AlphaStack.Application.Features.Auth;

// ─── Get Login URL ────────────────────────────────────────────────────────────

public record GetKiteLoginUrlQuery(Guid UserProfileId) : IRequest<string>;

public class GetKiteLoginUrlHandler : IRequestHandler<GetKiteLoginUrlQuery, string>
{
    private readonly IUserProfileRepository _userRepo;
    private readonly IKiteAuthService _kiteAuth;
    private readonly IEncryptionService _encryption;

    public GetKiteLoginUrlHandler(
        IUserProfileRepository userRepo,
        IKiteAuthService kiteAuth,
        IEncryptionService encryption)
    {
        _userRepo = userRepo;
        _kiteAuth = kiteAuth;
        _encryption = encryption;
    }

    public async Task<string> Handle(GetKiteLoginUrlQuery request, CancellationToken ct)
    {
        var user = await _userRepo.GetByIdAsync(request.UserProfileId, ct)
            ?? throw new InvalidOperationException($"User {request.UserProfileId} not found.");
        
        if (!user.HasZerodhaCredentials)
        throw new InvalidOperationException(
            "This user has no Zerodha credentials configured. Use Fyers for market data.");


        var apiKey = _encryption.Decrypt(user.EncryptedKiteApiKey);
        return _kiteAuth.GetLoginUrl(apiKey, request.UserProfileId.ToString());
    }
}

// ─── Complete Kite Auth (OAuth callback) ─────────────────────────────────────

public record CompleteKiteAuthCommand(Guid UserProfileId, string RequestToken) : IRequest<CompleteKiteAuthResult>;

public record CompleteKiteAuthResult(string UserId, DateTime ExpiresAt, bool Success);

public class CompleteKiteAuthHandler : IRequestHandler<CompleteKiteAuthCommand, CompleteKiteAuthResult>
{
    private readonly IUserProfileRepository _userRepo;
    private readonly IKiteAuthService _kiteAuth;
    private readonly IEncryptionService _encryption;
    private readonly IUnitOfWork _uow;

    public CompleteKiteAuthHandler(
        IUserProfileRepository userRepo,
        IKiteAuthService kiteAuth,
        IEncryptionService encryption,
        IUnitOfWork uow)
    {
        _userRepo = userRepo;
        _kiteAuth = kiteAuth;
        _encryption = encryption;
        _uow = uow;
    }

    public async Task<CompleteKiteAuthResult> Handle(CompleteKiteAuthCommand request, CancellationToken ct)
    {
        var user = await _userRepo.GetByIdAsync(request.UserProfileId, ct)
            ?? throw new InvalidOperationException($"User {request.UserProfileId} not found.");

        var apiKey = _encryption.Decrypt(user.EncryptedKiteApiKey);
        var apiSecret = _encryption.Decrypt(user.EncryptedKiteApiSecret);

        var session = await _kiteAuth.GenerateSessionAsync(apiKey, apiSecret, request.RequestToken, ct);

        user.SetKiteAccessToken(session.AccessToken, session.ExpiresAt);
        await _userRepo.UpdateAsync(user, ct);
        await _uow.SaveChangesAsync(ct);

        return new CompleteKiteAuthResult(session.UserId, session.ExpiresAt, Success: true);
    }
}

// ─── Check Session Status ─────────────────────────────────────────────────────

public record GetKiteSessionStatusQuery(Guid UserProfileId) : IRequest<KiteSessionStatus>;

public record KiteSessionStatus(bool IsValid, DateTime? ExpiresAt, string? Username);

public class GetKiteSessionStatusHandler : IRequestHandler<GetKiteSessionStatusQuery, KiteSessionStatus>
{
    private readonly IUserProfileRepository _userRepo;

    public GetKiteSessionStatusHandler(IUserProfileRepository userRepo) => _userRepo = userRepo;

    public async Task<KiteSessionStatus> Handle(GetKiteSessionStatusQuery request, CancellationToken ct)
    {
        var user = await _userRepo.GetByIdAsync(request.UserProfileId, ct)
            ?? throw new InvalidOperationException($"User {request.UserProfileId} not found.");

        return new KiteSessionStatus(
            IsValid: user.IsKiteSessionValid(),
            ExpiresAt: user.KiteAccessTokenExpiry,
            Username: user.Username);
    }
}
