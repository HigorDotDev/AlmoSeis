using AlmoSeis.Infrastructure.Audit;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Persistence.Entities;
using AlmoSeis.Infrastructure.Security;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Billing.Contracts;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.Modules.Identity.Domain;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Errors;
using AlmoSeis.SharedKernel.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AlmoSeis.Modules.Identity.Application;

/// POST /auth/signup (ADR-0018): usuário (novo ou existente), estabelecimento, teste grátis e vínculo OWNER.
internal sealed class SignUpHandler(
    AppDbContext db,
    ITenantContext tenant,
    ITenantSession session,
    IPasswordHasher<User> passwords,
    IBillingPort billing,
    IAuditWriter audit,
    AuthSessionIssuer sessions,
    IIdGenerator ids,
    TimeProvider time) : ICommandHandler<SignUpCommand, AuthResultDto>
{
    public async Task<AuthResultDto> HandleAsync(SignUpCommand command, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var user = command.ExistingAccount is { } existing
            ? await Credentials.VerifyAsync(db, passwords, existing.Login, existing.Password, now, cancellationToken)
                ?? throw new BusinessException(ErrorCodes.InvalidCredentials)
            : await Credentials.CreateUserAsync(db, passwords, ids, command.NewAccount!, now, cancellationToken);

        // O tenant nasce aqui: a sessão do banco passa a usá-lo antes de qualquer INSERT com RLS.
        var tenantId = ids.New();
        tenant.InitializeForTenant(tenantId, user.Id);
        await session.ApplyAsync(cancellationToken);

        var input = command.Tenant;
        var newTenant = Tenant.Create(
            tenantId, input.Name, input.DocumentType, input.DocumentNumber, input.City, input.State,
            input.TimeZone ?? "America/Sao_Paulo", command.AcceptedTermsVersion, user.Id, now);
        db.Add(newTenant);
        await db.SaveChangesAsync(cancellationToken); // usuário e tenant antes das tabelas que os referenciam

        db.Add(new TenantSyncCounter { TenantId = tenantId });
        db.Add(TenantSettings.CreateDefault(tenantId));
        var owner = TenantEmployee.Create(ids.New(), tenantId, user.Id, FirstName(user.FullName), [Role.Owner], now);
        db.Add(owner);

        await billing.StartTrialAsync(tenantId, ClaimSources(input, user), cancellationToken);

        audit.Add("TenantSignedUp", "TENANT", tenantId, after: new { tenantId, input.Name, termsVersion = command.AcceptedTermsVersion });
        return await sessions.IssueAsync(user, owner, command.InstallationId, null, null, cancellationToken);
    }

    private static string[] ClaimSources(TenantInput tenantInput, User user) =>
        user.PhoneNumber is null
            ? [$"doc:{tenantInput.DocumentNumber}"]
            : [$"doc:{tenantInput.DocumentNumber}", $"phone:{user.PhoneNumber}"];

    private static string FirstName(string fullName) => fullName.Trim().Split(' ', 2)[0];
}

/// POST /auth/login. Falhas também gravam (contador de tentativas), por isso devolvem AuthOutcome.
internal sealed class LoginHandler(
    AppDbContext db,
    ITenantContext tenant,
    ITenantSession session,
    IPasswordHasher<User> passwords,
    AuthSessionIssuer sessions,
    TimeProvider time) : ICommandHandler<LoginCommand, AuthOutcome<AuthResultDto>>
{
    public async Task<AuthOutcome<AuthResultDto>> HandleAsync(LoginCommand command, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var user = await Credentials.FindByLoginAsync(db, command.Login, cancellationToken);
        if (user is null)
        {
            return AuthOutcome<AuthResultDto>.Failure(ErrorCodes.InvalidCredentials.Code);
        }

        if (user.IsLockedOut(now))
        {
            return Locked(user);
        }

        if (passwords.VerifyHashedPassword(user, user.PasswordHash, command.Password) == PasswordVerificationResult.Failed)
        {
            user.RegisterFailedLogin(now);
            return user.IsLockedOut(now) ? Locked(user) : AuthOutcome<AuthResultDto>.Failure(ErrorCodes.InvalidCredentials.Code);
        }

        user.RegisterSuccessfulLogin();

        tenant.InitializeUserOnly(user.Id);
        await session.ApplyAsync(cancellationToken);

        var memberships = await sessions.ListMembershipsAsync(user.Id, cancellationToken);
        if (memberships.Count == 0)
        {
            return AuthOutcome<AuthResultDto>.Failure(ErrorCodes.NoActiveMembership.Code);
        }

        var chosen = memberships.FirstOrDefault(m => m.TenantId == command.PreferredTenantId) ?? memberships[0];
        tenant.InitializeForTenant(chosen.TenantId, user.Id);
        await session.ApplyAsync(cancellationToken);

        var employee = (await sessions.FindActiveEmployeeAsync(user.Id, chosen.TenantId, cancellationToken))!;
        return AuthOutcome<AuthResultDto>.Success(
            await sessions.IssueAsync(user, employee, command.InstallationId, null, null, cancellationToken));
    }

    private static AuthOutcome<AuthResultDto> Locked(User user) =>
        AuthOutcome<AuthResultDto>.Failure(
            ErrorCodes.InvalidCredentials.Code,
            new Dictionary<string, object?> { ["lockedUntil"] = user.LockoutEnd });
}

/// POST /auth/refresh: rotação; reuso de token já trocado revoga a família inteira (ADR-0027).
internal sealed class RefreshTokenHandler(
    AppDbContext db,
    ITenantContext tenant,
    ITenantSession session,
    ISecretHasher hasher,
    AuthSessionIssuer sessions,
    TimeProvider time) : ICommandHandler<RefreshTokenCommand, AuthOutcome<TokensDto>>
{
    public async Task<AuthOutcome<TokensDto>> HandleAsync(RefreshTokenCommand command, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var hash = hasher.Hash(command.RefreshToken);
        var current = await db.Set<RefreshToken>().SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (current is null)
        {
            return Invalid();
        }

        if (current.RevokedAt is not null)
        {
            await RevokeFamilyAsync(current.FamilyId, now, cancellationToken);
            return Invalid();
        }

        if (!current.IsUsable(now) || current.InstallationId != command.InstallationId)
        {
            return Invalid();
        }

        tenant.InitializeForTenant(current.TenantId, current.UserId);
        await session.ApplyAsync(cancellationToken);

        var user = await db.Set<User>().SingleAsync(u => u.Id == current.UserId, cancellationToken);
        var employee = await db.Set<TenantEmployee>().Include(e => e.Roles)
            .SingleOrDefaultAsync(e => e.Id == current.TenantEmployeeId, cancellationToken);
        if (employee is null || !employee.IsActive)
        {
            await RevokeFamilyAsync(current.FamilyId, now, cancellationToken);
            return Invalid();
        }

        var tokens = sessions.IssueTokens(user, employee, current.InstallationId, current.SharedDeviceId, current.FamilyId, out var next);
        current.Revoke(now, next.Id);
        return AuthOutcome<TokensDto>.Success(tokens);
    }

    private Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.Set<RefreshToken>()
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);

    private static AuthOutcome<TokensDto> Invalid() => AuthOutcome<TokensDto>.Failure(ErrorCodes.InvalidRefreshToken.Code);
}

/// POST /auth/logout: revoga o refresh informado (idempotente).
internal sealed class LogoutHandler(AppDbContext db, ISecretHasher hasher, TimeProvider time) : ICommandHandler<LogoutCommand, bool>
{
    public async Task<bool> HandleAsync(LogoutCommand command, CancellationToken cancellationToken)
    {
        var hash = hasher.Hash(command.RefreshToken);
        var token = await db.Set<RefreshToken>().SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        token?.Revoke(time.GetUtcNow());
        return true;
    }
}

/// POST /auth/switch-tenant: novos tokens para outro vínculo ativo do usuário.
internal sealed class SwitchTenantHandler(
    AppDbContext db,
    ITenantContext tenant,
    ITenantSession session,
    AuthSessionIssuer sessions) : ICommandHandler<SwitchTenantCommand, AuthResultDto>
{
    public async Task<AuthResultDto> HandleAsync(SwitchTenantCommand command, CancellationToken cancellationToken)
    {
        var userId = tenant.UserId ?? throw new BusinessException(ErrorCodes.Unauthenticated);
        var installationId = tenant.InstallationId ?? throw new BusinessException(ErrorCodes.Unauthenticated);

        var employee = await sessions.FindActiveEmployeeAsync(userId, command.TenantId, cancellationToken)
            ?? throw new NotFoundException("Estabelecimento", command.TenantId);

        tenant.SwitchTenant(command.TenantId, employee.Id);
        await session.ApplyAsync(cancellationToken);

        var user = await db.Set<User>().SingleAsync(u => u.Id == userId, cancellationToken);
        return await sessions.IssueAsync(user, employee, installationId, null, null, cancellationToken);
    }
}

internal static class Credentials
{
    public static async Task<User> CreateUserAsync(
        AppDbContext db, IPasswordHasher<User> passwords, IIdGenerator ids, NewAccountInput account, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var normalizedEmail = User.NormalizeEmail(account.Email);
        if (normalizedEmail is not null && await db.Set<User>().AnyAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken))
        {
            throw new BusinessException(ErrorCodes.EmailAlreadyRegistered);
        }

        if (account.PhoneNumber is not null && await db.Set<User>().AnyAsync(u => u.PhoneNumber == account.PhoneNumber, cancellationToken))
        {
            throw new BusinessException(ErrorCodes.PhoneAlreadyRegistered);
        }

        var user = User.Create(ids.New(), account.FullName, account.Email, account.PhoneNumber, now);
        user.SetPasswordHash(passwords.HashPassword(user, account.Password));
        db.Add(user);
        return user;
    }

    public static Task<User?> FindByLoginAsync(AppDbContext db, string login, CancellationToken cancellationToken)
    {
        var trimmed = login.Trim();
        if (trimmed.Contains('@', StringComparison.Ordinal))
        {
            var normalized = User.NormalizeEmail(trimmed);
            return db.Set<User>().SingleOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);
        }

        return db.Set<User>().SingleOrDefaultAsync(u => u.PhoneNumber == trimmed, cancellationToken);
    }

    public static async Task<User?> VerifyAsync(
        AppDbContext db, IPasswordHasher<User> passwords, string login, string password, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var user = await FindByLoginAsync(db, login, cancellationToken);
        if (user is null || user.IsLockedOut(now))
        {
            return null;
        }

        return passwords.VerifyHashedPassword(user, user.PasswordHash, password) == PasswordVerificationResult.Failed ? null : user;
    }
}
