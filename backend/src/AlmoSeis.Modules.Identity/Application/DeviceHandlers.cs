using System.Security.Cryptography;
using System.Text;
using AlmoSeis.Infrastructure.Audit;
using AlmoSeis.Infrastructure.Email;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Security;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.Modules.Identity.Domain;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Errors;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Modules.Identity.Application;

/// POST /devices/shared: registra a instalação atual. A mesma instalação registrada de novo revoga o registro
/// anterior e recebe um segredo novo (tablet reinstalado volta sem suporte).
internal sealed class RegisterSharedDeviceHandler(
    AppDbContext db, ITenantContext tenant, IIdGenerator ids, ISecretHasher hasher, IEmployeeDirectory employees, IAuditWriter audit, TimeProvider time)
    : ICommandHandler<RegisterSharedDeviceCommand, RegisteredSharedDeviceDto>
{
    public async Task<RegisteredSharedDeviceDto> HandleAsync(RegisterSharedDeviceCommand command, CancellationToken cancellationToken)
    {
        if (command.InstallationId != tenant.InstallationId)
        {
            throw BusinessException.Validation("installationId", "Registre o aparelho a partir dele mesmo.");
        }

        var now = time.GetUtcNow();
        var previous = await db.Set<SharedDevice>()
            .Where(d => d.InstallationId == command.InstallationId && d.RevokedAt == null)
            .Select(d => d.Id)
            .ToListAsync(cancellationToken);
        if (previous.Count > 0)
        {
            // Já, e não no commit: o índice único de instalação ativa vale por comando.
            await DeviceRules.RevokeAsync(db, previous, now, cancellationToken);
        }

        var secret = SecureRandom.Token("ds_");
        var device = SharedDevice.Register(ids.New(), tenant.TenantId!.Value, command.InstallationId, command.Name, command.Platform,
            hasher.Hash(secret), tenant.EmployeeId!.Value, now);
        db.Add(device);
        audit.Add("SharedDeviceRegistered", "SHARED_DEVICE", device.Id, after: new { device.Name, device.Platform, replaced = previous.Count });

        var names = await employees.GetDisplayNamesAsync([device.RegisteredByEmployeeId], cancellationToken);
        return new RegisteredSharedDeviceDto(device.Id, device.TenantId, device.InstallationId, device.Name, device.Platform, secret, device.RegisteredAt,
            new AuthorDto(device.RegisteredByEmployeeId, names.GetValueOrDefault(device.RegisteredByEmployeeId, string.Empty)));
    }
}

internal sealed class ListSharedDevicesQueryHandler(AppDbContext db, IEmployeeDirectory employees) : IQueryHandler<ListSharedDevicesQuery, ItemsDto<SharedDeviceDto>>
{
    public async Task<ItemsDto<SharedDeviceDto>> HandleAsync(ListSharedDevicesQuery query, CancellationToken cancellationToken)
    {
        var devices = await db.Set<SharedDevice>().AsNoTracking().OrderByDescending(d => d.RegisteredAt).ToListAsync(cancellationToken);
        var names = await employees.GetDisplayNamesAsync(devices.Select(d => d.RegisteredByEmployeeId), cancellationToken);
        return new ItemsDto<SharedDeviceDto>(devices
            .Select(d => new SharedDeviceDto(d.Id, d.Name, d.Platform, d.RegisteredAt,
                new AuthorDto(d.RegisteredByEmployeeId, names.GetValueOrDefault(d.RegisteredByEmployeeId, string.Empty)), d.RevokedAt))
            .ToList());
    }
}

/// POST /devices/shared/{id}/revoke: também revoga os refresh tokens emitidos por PIN nesse aparelho.
internal sealed class RevokeSharedDeviceHandler(AppDbContext db, IAuditWriter audit, TimeProvider time) : ICommandHandler<RevokeSharedDeviceCommand, bool>
{
    public async Task<bool> HandleAsync(RevokeSharedDeviceCommand command, CancellationToken cancellationToken)
    {
        if (!await db.Set<SharedDevice>().AnyAsync(d => d.Id == command.DeviceId, cancellationToken))
        {
            throw new NotFoundException("SHARED_DEVICE", command.DeviceId);
        }

        await DeviceRules.RevokeAsync(db, [command.DeviceId], time.GetUtcNow(), cancellationToken);
        audit.Add("SharedDeviceRevoked", "SHARED_DEVICE", command.DeviceId);
        return true;
    }
}

/// GET /devices/shared/{installationId}/employees (público + segredo do aparelho): quem pode entrar por PIN.
internal sealed class ListPinEmployeesQueryHandler(AppDbContext db, ITenantContext tenant, ISecretHasher hasher)
    : IQueryHandler<ListPinEmployeesQuery, DeviceEmployeesDto>
{
    public async Task<DeviceEmployeesDto> HandleAsync(ListPinEmployeesQuery query, CancellationToken cancellationToken)
    {
        tenant.InitializeForTenant(query.TenantId, Guid.Empty);
        var device = await DeviceRules.AuthenticateAsync(db, hasher, query.InstallationId, query.DeviceSecret, cancellationToken)
            ?? throw new BusinessException(ErrorCodes.InvalidDeviceSecret);

        var tenantName = await db.Set<Tenant>().AsNoTracking().Where(t => t.Id == query.TenantId).Select(t => t.Name).SingleAsync(cancellationToken);
        var employees = await db.Set<TenantEmployee>().AsNoTracking().Include(e => e.Roles)
            .Where(e => e.Status == EmployeeStatus.Active && e.PinHash != null)
            .OrderBy(e => e.DisplayName)
            .ToListAsync(cancellationToken);

        return new DeviceEmployeesDto(
            new DeviceRefDto(device.Id, device.Name),
            new DeviceTenantDto(query.TenantId, tenantName),
            employees.Select(e => new PinEmployeeDto(e.Id, e.DisplayName, e.Roles.Select(r => r.Role).Order().ToList())).ToList());
    }
}

/// POST /auth/pin-login (público + segredo do aparelho). Falhas gravam (tentativas e bloqueio), por isso AuthOutcome.
/// A sessão enxerga só o tenant do aparelho.
internal sealed class PinLoginHandler(
    AppDbContext db,
    ITenantContext tenant,
    ITenantSession session,
    ISecretHasher hasher,
    IPasswordHasher<User> passwords,
    AuthSessionIssuer sessions,
    TimeProvider time) : ICommandHandler<PinLoginCommand, AuthOutcome<AuthResultDto>>
{
    public async Task<AuthOutcome<AuthResultDto>> HandleAsync(PinLoginCommand command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        tenant.InitializeForTenant(request.TenantId, Guid.Empty);
        await session.ApplyAsync(cancellationToken);

        var device = await DeviceRules.AuthenticateAsync(db, hasher, request.InstallationId, command.DeviceSecret, cancellationToken);
        if (device is null)
        {
            return AuthOutcome<AuthResultDto>.Failure(ErrorCodes.InvalidDeviceSecret.Code);
        }

        var now = time.GetUtcNow();
        var employee = await db.Set<TenantEmployee>().Include(e => e.Roles)
            .SingleOrDefaultAsync(e => e.Id == request.EmployeeId && e.Status == EmployeeStatus.Active && e.PinHash != null, cancellationToken);
        if (employee is null)
        {
            return AuthOutcome<AuthResultDto>.Failure(ErrorCodes.InvalidCredentials.Code);
        }

        if (employee.IsPinLocked(now))
        {
            return Locked(employee);
        }

        var user = await db.Set<User>().SingleAsync(u => u.Id == employee.UserId, cancellationToken);
        if (passwords.VerifyHashedPassword(user, employee.PinHash!, request.Pin) == PasswordVerificationResult.Failed)
        {
            var attemptsLeft = employee.RegisterPinFailure(now);
            return attemptsLeft == 0
                ? Locked(employee)
                : AuthOutcome<AuthResultDto>.Failure(ErrorCodes.InvalidCredentials.Code, new Dictionary<string, object?> { ["attemptsLeft"] = attemptsLeft });
        }

        employee.RegisterPinSuccess();
        return AuthOutcome<AuthResultDto>.Success(await sessions.IssueAsync(user, employee, request.InstallationId, device.Id, null, cancellationToken));
    }

    private static AuthOutcome<AuthResultDto> Locked(TenantEmployee employee) =>
        AuthOutcome<AuthResultDto>.Failure(ErrorCodes.PinLocked.Code, new Dictionary<string, object?> { ["lockedUntil"] = employee.PinLockedUntil });
}

internal static class DeviceRules
{
    /// Aparelho ativo do tenant da sessão cujo hash confere com o segredo (comparação em tempo constante).
    public static async Task<SharedDevice?> AuthenticateAsync(
        AppDbContext db, ISecretHasher hasher, string installationId, string? secret, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return null;
        }

        var device = await db.Set<SharedDevice>().AsNoTracking()
            .SingleOrDefaultAsync(d => d.InstallationId == installationId && d.RevokedAt == null, cancellationToken);
        return device is not null
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(device.SecretHash), Encoding.ASCII.GetBytes(hasher.Hash(secret)))
            ? device
            : null;
    }

    public static async Task RevokeAsync(AppDbContext db, IReadOnlyCollection<Guid> deviceIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await db.Set<SharedDevice>().Where(d => deviceIds.Contains(d.Id) && d.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.RevokedAt, now), cancellationToken);
        await db.Set<RefreshToken>().Where(t => t.SharedDeviceId != null && deviceIds.Contains(t.SharedDeviceId.Value) && t.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.RevokedAt, now), cancellationToken);
    }
}

public sealed class PasswordResetOptions
{
    public const string Section = "PasswordReset";

    /// O link do e-mail é este prefixo + token.
    public string LinkBaseUrl { get; set; } = "https://almoseis.app/redefinir-senha?token=";
}

/// Token do e-mail de "esqueci a senha" (ADR-0040): protegido pelo Data Protection, vale 30 min e é de uso único,
/// porque carrega o security stamp do usuário, que muda quando a senha muda.
internal sealed class EmailResetTokens(IDataProtectionProvider provider, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private ITimeLimitedDataProtector Protector => provider.CreateProtector("AlmoSeis.PasswordReset.v1").ToTimeLimitedDataProtector();

    public string Create(User user) => Protector.Protect($"{user.Id:N}|{user.SecurityStamp}", time.GetUtcNow() + Lifetime);

    /// (usuário, stamp) ou null para token inválido ou vencido.
    public (Guid UserId, string Stamp)? Read(string token)
    {
        try
        {
            var parts = Protector.Unprotect(token, out var expiration).Split('|');
            return expiration > time.GetUtcNow() && parts.Length == 2 && Guid.TryParseExact(parts[0], "N", out var id) ? (id, parts[1]) : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

/// POST /auth/password/forgot: sempre 202, exista ou não o e-mail (não revela contas).
internal sealed class ForgotPasswordHandler(
    AppDbContext db, EmailResetTokens tokens, IEmailSender email, IOptions<PasswordResetOptions> options, ILogger<ForgotPasswordHandler> logger)
    : ICommandHandler<ForgotPasswordCommand, bool>
{
    public async Task<bool> HandleAsync(ForgotPasswordCommand command, CancellationToken cancellationToken)
    {
        var normalized = User.NormalizeEmail(command.Email);
        var user = await db.Set<User>().AsNoTracking().SingleOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);
        if (user?.Email is null)
        {
            return true;
        }

        var link = options.Value.LinkBaseUrl + Uri.EscapeDataString(tokens.Create(user));
        try
        {
            await email.SendAsync(user.Email, "Redefinição de senha do AlmoSeis",
                $"Olá, {user.FullName}.\n\nPara criar uma nova senha, abra o link abaixo em até 30 minutos:\n{link}\n\nSe não foi você, ignore este e-mail.",
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A resposta continua 202: falha de envio não pode revelar que a conta existe.
            logger.LogError(ex, "Falha ao enviar o e-mail de redefinição de senha.");
        }

        return true;
    }
}
