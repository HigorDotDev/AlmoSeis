using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.Modules.Notifications.Contracts;
using AlmoSeis.Modules.Notifications.Domain;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Errors;
using AlmoSeis.SharedKernel.Security;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AlmoSeis.Modules.Notifications.Application;

/// PUT /devices/push-token (06 §6.3, ADR-0049 item 4): registra ou atualiza o token desta instalação no estabelecimento
/// da sessão. Sem auditoria: é configuração técnica do aparelho, e o token não pode ir para a auditoria.
internal sealed class RegisterPushTokenHandler(
    AppDbContext db,
    ITenantContext tenant,
    IIdGenerator ids,
    IEmployeeDirectory employees,
    IPermissionChecker permissions,
    TimeProvider time) : ICommandHandler<RegisterPushTokenCommand, PushRegistrationDto>
{
    public async Task<PushRegistrationDto> HandleAsync(RegisterPushTokenCommand command, CancellationToken cancellationToken)
    {
        if (command.InstallationId != tenant.InstallationId)
        {
            throw BusinessException.Validation("installationId", "Registre o aparelho a partir dele mesmo.");
        }

        if (command.Platform == PushPlatform.Web)
        {
            throw BusinessException.Validation("platform", "O aviso com o navegador fechado ainda não está disponível.");
        }

        var tenantId = tenant.TenantId!.Value;
        var token = command.ExpoPushToken;

        // Dois registros simultâneos do mesmo token (dois estabelecimentos) esperam um pelo outro em vez de estourar o
        // índice único do token.
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended('push-token:' || {token}, 0))", cancellationToken);

        // O mesmo aparelho em outro estabelecimento (troca sem logout) sai de lá pela função `release_push_token`
        // (05 §5.17 0007): só quando o registro antigo é desta mesma instalação, sem a sessão enxergar a linha. O token
        // de outra instalação em outro tenant não é liberado: quem só conhece a string não tira o aviso de ninguém.
        var released = (await db.Database
            .SqlQuery<int>($"SELECT release_push_token({token}, {command.InstallationId}) AS \"Value\"")
            .ToListAsync(cancellationToken)).Single();
        if (released < 0)
        {
            throw new BusinessException(ErrorCodes.PushTokenInUse);
        }

        // Outra instalação deste estabelecimento com o mesmo token (app reinstalado): filtro do EF + RLS de tenant.
        await db.Set<PushRegistration>()
            .Where(r => r.ExpoPushToken == token && r.InstallationId != command.InstallationId)
            .ExecuteDeleteAsync(cancellationToken);

        // Aparelho compartilhado é de um estabelecimento só; fora dele, o título leva o nome quando o usuário tem mais de um.
        var showTenantName = tenant.SharedDeviceId is null && tenant.UserId is { } userId
            && await employees.CountActiveMembershipsAsync(userId, cancellationToken) > 1;

        var now = time.GetUtcNow();
        var registration = await db.Set<PushRegistration>().SingleOrDefaultAsync(r => r.InstallationId == command.InstallationId, cancellationToken);
        if (registration is null)
        {
            registration = PushRegistration.Create(ids.New(), tenantId, command.InstallationId, tenant.EmployeeId!.Value, tenant.SharedDeviceId,
                command.Platform, token, command.WebOrdersEnabled ?? true, showTenantName, now);
            db.Add(registration);
        }
        else
        {
            registration.Refresh(tenant.EmployeeId!.Value, tenant.SharedDeviceId, command.Platform, token, command.WebOrdersEnabled, showTenantName, now);
        }

        return new PushRegistrationDto(
            registration.InstallationId,
            registration.Platform,
            registration.WebOrdersEnabled,
            registration.WebOrdersEnabled && permissions.Has(Permissions.Orders.Accept),
            registration.CreatedAt,
            registration.UpdatedAt);
    }
}

internal sealed class RegisterPushTokenValidator : AbstractValidator<RegisterPushTokenCommand>
{
    public RegisterPushTokenValidator()
    {
        RuleFor(x => x.InstallationId).NotEmpty().MaximumLength(64);
        RuleFor(x => x.ExpoPushToken).Must(PushRegistration.IsExpoToken).WithMessage("Token de push do Expo inválido.");
    }
}

/// DELETE /devices/push-token: o aparelho desliga o push neste estabelecimento (idempotente).
internal sealed class RemovePushTokenHandler(AppDbContext db, ITenantContext tenant) : ICommandHandler<RemovePushTokenCommand, bool>
{
    public async Task<bool> HandleAsync(RemovePushTokenCommand command, CancellationToken cancellationToken)
    {
        var installationId = tenant.InstallationId ?? throw new BusinessException(ErrorCodes.Unauthenticated);
        await db.Set<PushRegistration>().Where(r => r.InstallationId == installationId).ExecuteDeleteAsync(cancellationToken);
        return true;
    }
}

/// Logout, troca de estabelecimento, `AccessRevoked` e revogação do aparelho apagam o registro (ADR-0049 item 4), na
/// transação de quem chamou e no tenant da sessão (filtro do EF + RLS).
internal sealed class PushRegistrationCleaner(AppDbContext db) : IDeviceAccessListener
{
    public Task OnInstallationSignedOutAsync(string installationId, CancellationToken cancellationToken) =>
        db.Set<PushRegistration>().Where(r => r.InstallationId == installationId).ExecuteDeleteAsync(cancellationToken);

    public Task OnEmployeeAccessRevokedAsync(Guid employeeId, CancellationToken cancellationToken) =>
        db.Set<PushRegistration>().Where(r => r.TenantEmployeeId == employeeId).ExecuteDeleteAsync(cancellationToken);

    public Task OnSharedDevicesRevokedAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken cancellationToken) =>
        db.Set<PushRegistration>()
            .Where(r => r.SharedDeviceId != null && deviceIds.Contains(r.SharedDeviceId.Value))
            .ExecuteDeleteAsync(cancellationToken);
}
