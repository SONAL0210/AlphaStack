using MediatR;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Domain.Entities;

namespace AlphaStack.Application.Features.Users;

// ─── Create User Profile ──────────────────────────────────────────────────────

public record CreateUserProfileCommand(
    string Username,
    string Email,
    string KiteApiKey,           // plaintext — encrypted before persistence
    string KiteApiSecret,        // plaintext
    string TelegramBotToken,     // plaintext
    long TelegramChatId,
    decimal TotalCapitalAllocated,
    decimal MaxDrawdownPercent,
    decimal MaxCapitalPerTradePercent
) : IRequest<Guid>;

public class CreateUserProfileHandler : IRequestHandler<CreateUserProfileCommand, Guid>
{
    private readonly IUserProfileRepository _userRepo;
    private readonly IEncryptionService _encryption;
    private readonly IUnitOfWork _uow;

    public CreateUserProfileHandler(
        IUserProfileRepository userRepo,
        IEncryptionService encryption,
        IUnitOfWork uow)
    {
        _userRepo = userRepo;
        _encryption = encryption;
        _uow = uow;
    }

    public async Task<Guid> Handle(CreateUserProfileCommand request, CancellationToken ct)
    {
        var existing = await _userRepo.GetByUsernameAsync(request.Username, ct);
        if (existing is not null)
            throw new InvalidOperationException($"Username '{request.Username}' is already taken.");

        // Declare variables BEFORE the method call
        var encryptedKiteApiKey = string.IsNullOrEmpty(request.KiteApiKey)
            ? null : _encryption.Encrypt(request.KiteApiKey);

        var encryptedKiteApiSecret = string.IsNullOrEmpty(request.KiteApiSecret)
            ? null : _encryption.Encrypt(request.KiteApiSecret);

        var user = UserProfile.Create(
            username: request.Username,
            email: request.Email,
            encryptedKiteApiKey: encryptedKiteApiKey,
            encryptedKiteApiSecret: encryptedKiteApiSecret,
            encryptedTelegramBotToken: _encryption.Encrypt(request.TelegramBotToken),
            telegramChatId: request.TelegramChatId,
            totalCapitalAllocated: request.TotalCapitalAllocated,
            maxDrawdownPercent: request.MaxDrawdownPercent,
            maxCapitalPerTradePercent: request.MaxCapitalPerTradePercent
        );

        await _userRepo.AddAsync(user, ct);
        await _uow.SaveChangesAsync(ct);

        return user.Id;
    }
}

public record UpdateTelegramCredentialsCommand(
    Guid UserProfileId,
    string TelegramBotToken,
    long TelegramChatId
) : IRequest;

public class UpdateTelegramCredentialsHandler : IRequestHandler<UpdateTelegramCredentialsCommand>
{
    private readonly IUserProfileRepository _userRepo;
    private readonly IEncryptionService _encryption;
    private readonly IUnitOfWork _uow;

    public UpdateTelegramCredentialsHandler(
        IUserProfileRepository userRepo,
        IEncryptionService encryption,
        IUnitOfWork uow)
    {
        _userRepo = userRepo;
        _encryption = encryption;
        _uow = uow;
    }

    public async Task Handle(UpdateTelegramCredentialsCommand request, CancellationToken ct)
    {
        var user = await _userRepo.GetByIdAsync(request.UserProfileId, ct)
            ?? throw new InvalidOperationException($"User {request.UserProfileId} not found.");

        user.UpdateTelegramCredentials(
            _encryption.Encrypt(request.TelegramBotToken),
            request.TelegramChatId);

        await _userRepo.UpdateAsync(user, ct);
        await _uow.SaveChangesAsync(ct);
    }
}

// ─── Get User Profile ─────────────────────────────────────────────────────────

public record GetUserProfileQuery(Guid UserProfileId) : IRequest<UserProfileDto>;

public record UserProfileDto(
    Guid Id,
    string Username,
    string Email,
    bool IsActive,
    bool HasValidKiteSession,
    DateTime? KiteSessionExpiry,
    decimal TotalCapitalAllocated,
    decimal MaxDrawdownPercent,
    decimal MaxCapitalPerTradePercent,
    int ActiveExecutionCount);

public class GetUserProfileHandler : IRequestHandler<GetUserProfileQuery, UserProfileDto>
{
    private readonly IUserProfileRepository _userRepo;

    public GetUserProfileHandler(IUserProfileRepository userRepo) => _userRepo = userRepo;

    public async Task<UserProfileDto> Handle(GetUserProfileQuery request, CancellationToken ct)
    {
        var user = await _userRepo.GetByIdAsync(request.UserProfileId, ct)
            ?? throw new InvalidOperationException($"User {request.UserProfileId} not found.");

        return new UserProfileDto(
            Id: user.Id,
            Username: user.Username,
            Email: user.Email,
            IsActive: user.IsActive,
            HasValidKiteSession: user.IsKiteSessionValid(),
            KiteSessionExpiry: user.KiteAccessTokenExpiry,
            TotalCapitalAllocated: user.TotalCapitalAllocated,
            MaxDrawdownPercent: user.MaxDrawdownPercent,
            MaxCapitalPerTradePercent: user.MaxCapitalPerTradePercent,
            ActiveExecutionCount: user.StrategyExecutions.Count(e => e.IsRunning)
        );
    }
}

// ─── Enroll User in Strategy ──────────────────────────────────────────────────

public record EnrollUserInStrategyCommand(
    Guid UserProfileId,
    Guid StrategyDefinitionId,
    Domain.Enums.ExecutionMode Mode,
    decimal AllocatedCapital
) : IRequest<Guid>;

public class EnrollUserInStrategyHandler : IRequestHandler<EnrollUserInStrategyCommand, Guid>
{
    private readonly IUserProfileRepository _userRepo;
    private readonly IStrategyDefinitionRepository _strategyRepo;
    private readonly IStrategyExecutionRepository _executionRepo;
    private readonly IUnitOfWork _uow;

    public EnrollUserInStrategyHandler(
        IUserProfileRepository userRepo,
        IStrategyDefinitionRepository strategyRepo,
        IStrategyExecutionRepository executionRepo,
        IUnitOfWork uow)
    {
        _userRepo = userRepo;
        _strategyRepo = strategyRepo;
        _executionRepo = executionRepo;
        _uow = uow;
    }

    public async Task<Guid> Handle(EnrollUserInStrategyCommand request, CancellationToken ct)
    {
        var user = await _userRepo.GetByIdAsync(request.UserProfileId, ct)
            ?? throw new InvalidOperationException($"User {request.UserProfileId} not found.");

        var strategy = await _strategyRepo.GetByIdAsync(request.StrategyDefinitionId, ct)
            ?? throw new InvalidOperationException($"Strategy {request.StrategyDefinitionId} not found.");

        // Validate capital fits within user's total allocation
        var existingExecutions = await _executionRepo.GetByUserAsync(user.Id, ct);
        var alreadyAllocated = existingExecutions.Sum(e => e.AllocatedCapital);
        if (alreadyAllocated + request.AllocatedCapital > user.TotalCapitalAllocated)
            throw new InvalidOperationException(
                $"Insufficient capital. Allocated: {alreadyAllocated}, Requested: {request.AllocatedCapital}, Total: {user.TotalCapitalAllocated}");

        var execution = StrategyExecution.Create(
            userProfileId: user.Id,
            strategyDefinitionId: strategy.Id,
            mode: request.Mode,
            allocatedCapital: request.AllocatedCapital
        );

        await _executionRepo.AddAsync(execution, ct);
        await _uow.SaveChangesAsync(ct);

        return execution.Id;
    }
}
