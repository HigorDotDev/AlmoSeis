using AlmoSeis.Infrastructure.Audit;
using AlmoSeis.Infrastructure.Concurrency;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.Modules.Identity.Domain;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;
using AlmoSeis.SharedKernel.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Modules.Identity.Application;

public sealed class InvitationOptions
{
    public const string Section = "Invitations";

    /// O link do convite é este prefixo + código (06 §6.4).
    public string LinkBaseUrl { get; set; } = "https://almoseis.app/convite/";
}

// ---------- Estabelecimento ----------

internal sealed class GetTenantQueryHandler(AppDbContext db, ITenantContext tenant) : IQueryHandler<GetTenantQuery, TenantDto>
{
    public async Task<TenantDto> HandleAsync(GetTenantQuery query, CancellationToken cancellationToken) =>
        ManagementDtos.Tenant(await db.Set<Tenant>().AsNoTracking().SingleAsync(t => t.Id == tenant.TenantId, cancellationToken));
}

internal sealed class UpdateTenantHandler(AppDbContext db, ITenantContext tenant, IConcurrencyContext concurrency, IAuditWriter audit)
    : ICommandHandler<UpdateTenantCommand, TenantDto>
{
    public async Task<TenantDto> HandleAsync(UpdateTenantCommand command, CancellationToken cancellationToken)
    {
        var t = await db.Set<Tenant>().SingleAsync(x => x.Id == tenant.TenantId, cancellationToken);
        t.EnsureVersion(concurrency.ExpectedVersion);

        var c = command.Changes;
        var timeZone = c.TimeZone.GetValueOr(t.TimeZone);
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _))
        {
            throw BusinessException.Validation("timeZone", "Fuso horário desconhecido. Use o formato IANA, ex.: America/Sao_Paulo.");
        }

        var before = new { t.Name, t.City, t.State, t.TimeZone };
        if (t.Update(c.Name.GetValueOr(t.Name), c.City.GetValueOr(t.City), c.State.GetValueOr(t.State), timeZone))
        {
            audit.Add("TenantUpdated", "TENANT", t.Id, before: before, after: new { t.Name, t.City, t.State, t.TimeZone });
        }

        return ManagementDtos.Tenant(t);
    }
}

internal sealed class GetTenantSettingsQueryHandler(AppDbContext db, ITenantContext tenant) : IQueryHandler<GetTenantSettingsQuery, TenantSettingsDto>
{
    public async Task<TenantSettingsDto> HandleAsync(GetTenantSettingsQuery query, CancellationToken cancellationToken) =>
        ManagementDtos.Settings(await db.Set<TenantSettings>().AsNoTracking().SingleAsync(s => s.Id == tenant.TenantId, cancellationToken));
}

internal sealed class UpdateTenantSettingsHandler(
    AppDbContext db, ITenantContext tenant, IConcurrencyContext concurrency, IAuditWriter audit, TimeProvider time)
    : ICommandHandler<UpdateTenantSettingsCommand, TenantSettingsDto>
{
    public async Task<TenantSettingsDto> HandleAsync(UpdateTenantSettingsCommand command, CancellationToken cancellationToken)
    {
        var s = await db.Set<TenantSettings>().SingleAsync(x => x.Id == tenant.TenantId, cancellationToken);
        s.EnsureVersion(concurrency.ExpectedVersion);

        var c = command.Changes;
        var before = Audit(s);
        if (s.Update(
                c.KiloPricePerKg.GetValueOr(s.KiloPricePerKg),
                c.DefaultDeliveryFee.GetValueOr(s.DefaultDeliveryFee),
                c.EnabledPaymentMethods.HasValue ? c.EnabledPaymentMethods.Value : s.EnabledPaymentMethods,
                c.SharedDeviceIdleSeconds.GetValueOr(s.SharedDeviceIdleSeconds),
                time.GetUtcNow()))
        {
            audit.Add("TenantSettingsUpdated", s.SyncEntityType, s.Id, before: before, after: Audit(s));
        }

        return ManagementDtos.Settings(s);
    }

    private static object Audit(TenantSettings s) => new
    {
        kiloPricePerKg = s.KiloPricePerKg?.ToString(),
        defaultDeliveryFee = s.DefaultDeliveryFee.ToString(),
        enabledPaymentMethods = s.EnabledPaymentMethods.ToList(),
        s.SharedDeviceIdleSeconds,
    };
}

internal sealed class TenantCurrentReader(IQueryHandler<GetTenantQuery, TenantDto> query) : ICurrentResourceReader
{
    public string EntityType => "TENANT";

    public async Task<IVersionedDto?> ReadAsync(Guid entityId, CancellationToken cancellationToken) =>
        await query.HandleAsync(new GetTenantQuery(), cancellationToken);
}

internal sealed class TenantSettingsCurrentReader(IQueryHandler<GetTenantSettingsQuery, TenantSettingsDto> query) : ICurrentResourceReader
{
    public string EntityType => "TENANT_SETTINGS";

    public async Task<IVersionedDto?> ReadAsync(Guid entityId, CancellationToken cancellationToken) =>
        await query.HandleAsync(new GetTenantSettingsQuery(), cancellationToken);
}

// ---------- Funcionários ----------

internal sealed class ListEmployeesQueryHandler(AppDbContext db, ManagementDtos dtos) : IQueryHandler<ListEmployeesQuery, ItemsDto<EmployeeDto>>
{
    public async Task<ItemsDto<EmployeeDto>> HandleAsync(ListEmployeesQuery query, CancellationToken cancellationToken)
    {
        var employees = db.Set<TenantEmployee>().AsNoTracking().Include(e => e.Roles).AsQueryable();
        if (query.Status is { } status)
        {
            employees = employees.Where(e => e.Status == status);
        }

        var rows = await employees.OrderBy(e => e.DisplayName).ThenBy(e => e.Id).ToListAsync(cancellationToken);
        return new ItemsDto<EmployeeDto>(await dtos.EmployeesAsync(rows, cancellationToken));
    }
}

internal sealed class GetEmployeeQueryHandler(AppDbContext db, ManagementDtos dtos) : IQueryHandler<GetEmployeeQuery, EmployeeDto>
{
    public async Task<EmployeeDto> HandleAsync(GetEmployeeQuery query, CancellationToken cancellationToken)
    {
        var employee = await db.Set<TenantEmployee>().AsNoTracking().Include(e => e.Roles)
            .SingleOrDefaultAsync(e => e.Id == query.EmployeeId, cancellationToken)
            ?? throw new NotFoundException("EMPLOYEE", query.EmployeeId);
        return (await dtos.EmployeesAsync([employee], cancellationToken))[0];
    }
}

internal sealed class EmployeeCurrentReader(IQueryHandler<GetEmployeeQuery, EmployeeDto> query) : ICurrentResourceReader
{
    public string EntityType => "EMPLOYEE";

    public async Task<IVersionedDto?> ReadAsync(Guid entityId, CancellationToken cancellationToken) =>
        await query.HandleAsync(new GetEmployeeQuery(entityId), cancellationToken);
}

/// PATCH /employees/{id} [M]: nome e perfis. Tirar o último OWNER ativo → 409 LastOwner.
internal sealed class UpdateEmployeeHandler(
    AppDbContext db, IConcurrencyContext concurrency, EmployeeAccessRevoker revoker, IAuditWriter audit, ManagementDtos dtos)
    : ICommandHandler<UpdateEmployeeCommand, EmployeeDto>
{
    public async Task<EmployeeDto> HandleAsync(UpdateEmployeeCommand command, CancellationToken cancellationToken)
    {
        var employee = await EmployeeRules.LoadAsync(db, command.EmployeeId, cancellationToken);
        employee.EnsureVersion(concurrency.ExpectedVersion);

        var c = command.Changes;
        var beforeRoles = employee.Roles.Select(r => r.Role).Order().ToList();
        if (c.DisplayName.HasValue)
        {
            employee.Rename(c.DisplayName.Value);
        }

        if (c.Roles.HasValue)
        {
            var roles = EmployeeRules.ParseRoles(c.Roles.Value, "roles");
            if (employee.IsOwner && employee.IsActive && !roles.Contains(Role.Owner))
            {
                await EmployeeRules.EnsureAnotherOwnerAsync(db, employee.Id, cancellationToken);
            }

            if (employee.AssignRoles(roles))
            {
                audit.Add("EmployeeRolesChanged", employee.SyncEntityType, employee.Id,
                    before: new { roles = beforeRoles }, after: new { roles = employee.Roles.Select(r => r.Role).Order().ToList() });
                revoker.Revoke(employee, "ROLES_CHANGED");
            }
        }

        return (await dtos.EmployeesAsync([employee], cancellationToken))[0];
    }
}

/// POST /employees/{id}/deactivate [M]: acesso revogado em até 30 s (ADR-0027) e refresh tokens revogados.
internal sealed class DeactivateEmployeeHandler(
    AppDbContext db, ITenantContext tenant, IConcurrencyContext concurrency, EmployeeAccessRevoker revoker, IAuditWriter audit, ManagementDtos dtos, TimeProvider time)
    : ICommandHandler<DeactivateEmployeeCommand, EmployeeDto>
{
    public async Task<EmployeeDto> HandleAsync(DeactivateEmployeeCommand command, CancellationToken cancellationToken)
    {
        var employee = await EmployeeRules.LoadAsync(db, command.EmployeeId, cancellationToken);
        employee.EnsureVersion(concurrency.ExpectedVersion);
        if (employee.IsOwner && employee.IsActive)
        {
            await EmployeeRules.EnsureAnotherOwnerAsync(db, employee.Id, cancellationToken);
        }

        var now = time.GetUtcNow();
        if (employee.Deactivate(tenant.EmployeeId!.Value, now))
        {
            await db.Set<RefreshToken>()
                .Where(t => t.TenantEmployeeId == employee.Id && t.RevokedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(t => t.RevokedAt, now), cancellationToken);
            audit.Add("EmployeeDeactivated", employee.SyncEntityType, employee.Id, after: new { status = employee.Status });
            revoker.Revoke(employee, "DEACTIVATED");
        }

        return (await dtos.EmployeesAsync([employee], cancellationToken))[0];
    }
}

internal sealed class ReactivateEmployeeHandler(
    AppDbContext db, IConcurrencyContext concurrency, EmployeeAccessRevoker revoker, IAuditWriter audit, ManagementDtos dtos)
    : ICommandHandler<ReactivateEmployeeCommand, EmployeeDto>
{
    public async Task<EmployeeDto> HandleAsync(ReactivateEmployeeCommand command, CancellationToken cancellationToken)
    {
        var employee = await EmployeeRules.LoadAsync(db, command.EmployeeId, cancellationToken);
        employee.EnsureVersion(concurrency.ExpectedVersion);
        if (employee.Reactivate())
        {
            audit.Add("EmployeeReactivated", employee.SyncEntityType, employee.Id, after: new { status = employee.Status });
            revoker.Invalidate(employee);
        }

        return (await dtos.EmployeesAsync([employee], cancellationToken))[0];
    }
}

/// Invalida o cache de acesso e avisa o aparelho do funcionário (`AccessRevoked`, 06 §6.14).
internal sealed class EmployeeAccessRevoker(IEmployeeAccessInvalidator cache, IUnitOfWork unitOfWork, ITenantContext tenant, TimeProvider time)
{
    public void Revoke(TenantEmployee employee, string reason)
    {
        Invalidate(employee);
        unitOfWork.Enqueue(new IntegrationEnvelope("AccessRevoked", employee.SyncEntityType, employee.Id, tenant.EmployeeId, new { reason }), time.GetUtcNow());
    }

    public void Invalidate(TenantEmployee employee) => cache.Invalidate(employee.Id);
}

internal static class EmployeeRules
{
    public static async Task<TenantEmployee> LoadAsync(AppDbContext db, Guid employeeId, CancellationToken cancellationToken) =>
        await db.Set<TenantEmployee>().Include(e => e.Roles).SingleOrDefaultAsync(e => e.Id == employeeId, cancellationToken)
        ?? throw new NotFoundException("EMPLOYEE", employeeId);

    public static IReadOnlyList<Role> ParseRoles(IReadOnlyList<string> roles, string field)
    {
        var parsed = new List<Role>();
        foreach (var name in roles)
        {
            if (!Enum.GetValues<Role>().Any(r => r.ToApiName() == name))
            {
                throw BusinessException.Validation(field, $"Perfil desconhecido: {name}.");
            }

            parsed.Add(RolePermissions.FromApiName(name));
        }

        return parsed.Count == 0 ? throw BusinessException.Validation(field, "Escolha pelo menos um perfil.") : parsed;
    }

    /// O estabelecimento precisa de pelo menos um proprietário ativo (06 §6.1).
    public static async Task EnsureAnotherOwnerAsync(AppDbContext db, Guid employeeId, CancellationToken cancellationToken)
    {
        var owner = Role.Owner.ToApiName();
        var others = await db.Set<TenantEmployee>()
            .AnyAsync(e => e.Id != employeeId && e.Status == EmployeeStatus.Active && e.Roles.Any(r => r.Role == owner), cancellationToken);
        if (!others)
        {
            throw new BusinessException(ErrorCodes.LastOwner);
        }
    }
}

// ---------- Convites e redefinição de senha ----------

internal sealed class CreateInvitationHandler(InvitationWriter writer, ManagementDtos dtos) : ICommandHandler<CreateInvitationCommand, InvitationDto>
{
    public async Task<InvitationDto> HandleAsync(CreateInvitationCommand command, CancellationToken cancellationToken)
    {
        var roles = EmployeeRules.ParseRoles(command.Roles, "roles");
        var invitation = await writer.CreateAsync(InvitationPurpose.Invite, roles.Select(r => r.ToApiName()).Distinct().Order().ToArray(), null, cancellationToken);
        return (await dtos.InvitationsAsync([invitation], cancellationToken))[0];
    }
}

/// POST /employees/{id}/password-reset-code: código de uso único que o funcionário usa em /auth/password/reset.
internal sealed class IssuePasswordResetCodeHandler(AppDbContext db, InvitationWriter writer, ManagementDtos dtos)
    : ICommandHandler<IssuePasswordResetCodeCommand, InvitationDto>
{
    public async Task<InvitationDto> HandleAsync(IssuePasswordResetCodeCommand command, CancellationToken cancellationToken)
    {
        var employee = await EmployeeRules.LoadAsync(db, command.EmployeeId, cancellationToken);
        if (!employee.IsActive)
        {
            throw BusinessException.Validation("employeeId", "Funcionário inativo.");
        }

        var invitation = await writer.CreateAsync(InvitationPurpose.PasswordReset, null, employee.Id, cancellationToken);
        return (await dtos.InvitationsAsync([invitation], cancellationToken))[0];
    }
}

internal sealed class ListInvitationsQueryHandler(AppDbContext db, ManagementDtos dtos, TimeProvider time)
    : IQueryHandler<ListInvitationsQuery, ItemsDto<InvitationDto>>
{
    public async Task<ItemsDto<InvitationDto>> HandleAsync(ListInvitationsQuery query, CancellationToken cancellationToken)
    {
        var rows = await db.Set<EmployeeInvitation>().AsNoTracking().OrderByDescending(i => i.CreatedAt).Take(200).ToListAsync(cancellationToken);
        var now = time.GetUtcNow();
        var filtered = query.Status is { } status ? rows.Where(i => i.EffectiveStatus(now) == status).ToList() : rows;
        return new ItemsDto<InvitationDto>(await dtos.InvitationsAsync(filtered, cancellationToken));
    }
}

internal sealed class RevokeInvitationHandler(AppDbContext db, IAuditWriter audit, TimeProvider time) : ICommandHandler<RevokeInvitationCommand, bool>
{
    public async Task<bool> HandleAsync(RevokeInvitationCommand command, CancellationToken cancellationToken)
    {
        var invitation = await db.Set<EmployeeInvitation>().SingleOrDefaultAsync(i => i.Id == command.InvitationId, cancellationToken)
            ?? throw new NotFoundException("INVITATION", command.InvitationId);
        invitation.Revoke(time.GetUtcNow());
        audit.Add("InvitationRevoked", "INVITATION", invitation.Id, after: new { invitation.Purpose });
        return true;
    }
}

/// GET /invitations/{code} (público). Inexistente, vencido ou usado → 404, sem diferenciar (06 §6.4).
internal sealed class GetInvitationByCodeQueryHandler(AppDbContext db, ITenantContext tenant, ITenantSession session, InvitationLookup lookup)
    : IQueryHandler<GetInvitationByCodeQuery, PublicInvitationDto>
{
    public async Task<PublicInvitationDto> HandleAsync(GetInvitationByCodeQuery query, CancellationToken cancellationToken)
    {
        // Uma conexão só: as variáveis de sessão (código e tenant) valem até o fim da leitura.
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var invitation = await lookup.FindUsableAsync(query.Code, InvitationPurpose.Invite, cancellationToken)
                ?? throw new NotFoundException("INVITATION");

            // Nome do estabelecimento e de quem convidou: leitura no tenant do convite, sem usuário.
            tenant.InitializeForTenant(invitation.TenantId, Guid.Empty);
            await session.ApplyAsync(cancellationToken);
            var tenantName = await db.Set<Tenant>().AsNoTracking().Where(t => t.Id == invitation.TenantId).Select(t => t.Name).SingleAsync(cancellationToken);
            var invitedBy = await db.Set<TenantEmployee>().AsNoTracking()
                .Where(e => e.Id == invitation.CreatedByEmployeeId).Select(e => e.DisplayName).SingleAsync(cancellationToken);
            return new PublicInvitationDto(invitation.Code, invitation.Purpose, tenantName, invitation.Roles ?? [], invitedBy, invitation.ExpiresAt);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}

/// POST /invitations/{code}/accept (público): conta nova ou existente, vínculo no tenant do convite, sessão emitida.
internal sealed class AcceptInvitationHandler(
    AppDbContext db,
    ITenantContext tenant,
    ITenantSession session,
    InvitationLookup lookup,
    IPasswordHasher<User> passwords,
    AuthSessionIssuer sessions,
    IAuditWriter audit,
    IIdGenerator ids,
    TimeProvider time) : ICommandHandler<AcceptInvitationCommand, AuthResultDto>
{
    public async Task<AuthResultDto> HandleAsync(AcceptInvitationCommand command, CancellationToken cancellationToken)
    {
        var invitation = await lookup.FindUsableAsync(command.Code, InvitationPurpose.Invite, cancellationToken)
            ?? throw new NotFoundException("INVITATION");

        var now = time.GetUtcNow();
        var request = command.Request;
        var user = request.ExistingAccount is { } existing
            ? await Credentials.VerifyAsync(db, passwords, existing.Login, existing.Password, now, cancellationToken)
                ?? throw new BusinessException(ErrorCodes.InvalidCredentials)
            : await Credentials.CreateUserAsync(db, passwords, ids, request.NewAccount!, now, cancellationToken);

        // Lido pelo código; a partir daqui tudo é gravado no tenant do convite.
        tenant.InitializeForTenant(invitation.TenantId, user.Id);
        await session.ApplyAsync(cancellationToken);
        var tracked = await db.Set<EmployeeInvitation>().SingleAsync(i => i.Id == invitation.Id, cancellationToken);
        tracked.Accept(user.Id, now);

        var roles = tracked.RoleValues;
        var employee = await db.Set<TenantEmployee>().Include(e => e.Roles).SingleOrDefaultAsync(e => e.UserId == user.Id, cancellationToken);
        if (employee is null)
        {
            employee = TenantEmployee.Create(ids.New(), invitation.TenantId, user.Id, request.DisplayName, roles, now);
            db.Add(employee);
        }
        else if (employee.IsActive)
        {
            throw BusinessException.Validation("existingAccount", "Você já faz parte deste estabelecimento.");
        }
        else
        {
            employee.Rejoin(roles, request.DisplayName);
        }

        audit.Add("InvitationAccepted", "INVITATION", tracked.Id, after: new { employeeId = employee.Id, roles = tracked.Roles });
        return await sessions.IssueAsync(user, employee, request.InstallationId, null, null, cancellationToken);
    }
}

/// POST /auth/password/reset com código: confere o login do funcionário alvo, troca a senha e revoga as sessões.
internal sealed class ResetPasswordHandler(
    AppDbContext db,
    ITenantContext tenant,
    ITenantSession session,
    InvitationLookup lookup,
    EmailResetTokens emailTokens,
    IPasswordHasher<User> passwords,
    IAuditWriter audit,
    TimeProvider time) : ICommandHandler<ResetPasswordCommand, bool>
{
    public async Task<bool> HandleAsync(ResetPasswordCommand command, CancellationToken cancellationToken)
    {
        if (command.EmailToken is { } token)
        {
            return await ResetByEmailAsync(token, command.NewPassword, cancellationToken);
        }

        if (command.Code is null || command.Login is null)
        {
            throw BusinessException.Validation("code", "Informe o código e o login.");
        }

        var invitation = await lookup.FindUsableAsync(command.Code, InvitationPurpose.PasswordReset, cancellationToken)
            ?? throw new BusinessException(ErrorCodes.InvitationNotPending);
        var user = await Credentials.FindByLoginAsync(db, command.Login, cancellationToken);

        tenant.InitializeForTenant(invitation.TenantId, user?.Id ?? Guid.Empty);
        await session.ApplyAsync(cancellationToken);
        var target = await db.Set<TenantEmployee>().AsNoTracking().SingleAsync(e => e.Id == invitation.TargetEmployeeId, cancellationToken);
        if (user is null || target.UserId != user.Id)
        {
            // Mesmo erro para código certo com login errado: não confirma a quem o código pertence.
            throw new BusinessException(ErrorCodes.InvitationNotPending);
        }

        var now = time.GetUtcNow();
        var tracked = await db.Set<EmployeeInvitation>().SingleAsync(i => i.Id == invitation.Id, cancellationToken);
        tracked.Accept(user.Id, now);
        user.SetPasswordHash(passwords.HashPassword(user, command.NewPassword));
        await db.Set<RefreshToken>().Where(t => t.UserId == user.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.RevokedAt, now), cancellationToken);

        audit.Add("PasswordReset", "USER", user.Id, after: new { via = "CODE" });
        return true;
    }

    /// Link do e-mail (ADR-0040): uso único pelo security stamp. Sem tenant, então sem auditoria de tenant.
    private async Task<bool> ResetByEmailAsync(string token, string newPassword, CancellationToken cancellationToken)
    {
        var data = emailTokens.Read(token);
        var user = data is { } d ? await db.Set<User>().SingleOrDefaultAsync(u => u.Id == d.UserId, cancellationToken) : null;
        if (user is null || user.SecurityStamp != data!.Value.Stamp)
        {
            throw BusinessException.Validation("emailToken", "Link de redefinição inválido ou vencido. Peça outro.");
        }

        user.SetPasswordHash(passwords.HashPassword(user, newPassword));
        var now = time.GetUtcNow();
        await db.Set<RefreshToken>().Where(t => t.UserId == user.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.RevokedAt, now), cancellationToken);
        return true;
    }
}

/// PUT /auth/me/pin: confere a senha atual e aplica a política de PIN.
internal sealed class SetOwnPinHandler(AppDbContext db, ITenantContext tenant, IPasswordHasher<User> passwords, IAuditWriter audit)
    : ICommandHandler<SetOwnPinCommand, bool>
{
    public async Task<bool> HandleAsync(SetOwnPinCommand command, CancellationToken cancellationToken)
    {
        var user = await db.Set<User>().SingleAsync(u => u.Id == tenant.UserId, cancellationToken);
        if (passwords.VerifyHashedPassword(user, user.PasswordHash, command.CurrentPassword) == PasswordVerificationResult.Failed)
        {
            throw BusinessException.Validation("currentPassword", "Senha atual incorreta.");
        }

        if (PinPolicy.Problem(command.Pin) is { } problem)
        {
            throw BusinessException.Validation("pin", problem);
        }

        var employee = await db.Set<TenantEmployee>().SingleAsync(e => e.Id == tenant.EmployeeId, cancellationToken);
        employee.SetPin(passwords.HashPassword(user, command.Pin));
        audit.Add("EmployeePinChanged", employee.SyncEntityType, employee.Id);
        return true;
    }
}

/// Convite pelo código: a política `invitation_by_code` (05 §5.12) libera a leitura sem tenant na sessão.
internal sealed class InvitationLookup(AppDbContext db, ITenantSession session, TimeProvider time)
{
    public async Task<EmployeeInvitation?> FindUsableAsync(string code, string purpose, CancellationToken cancellationToken)
    {
        var normalized = code.Trim().ToUpperInvariant();
        await session.SetInvitationCodeAsync(normalized, cancellationToken);
        var invitation = await db.Set<EmployeeInvitation>().IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(i => i.Code == normalized && i.Status == InvitationStatus.Pending, cancellationToken);
        await session.SetInvitationCodeAsync(null, cancellationToken);
        return invitation is not null && invitation.Purpose == purpose && invitation.IsUsable(time.GetUtcNow()) ? invitation : null;
    }
}

/// Cria o convite por SQL: o código é único entre os pendentes de todos os tenants, e a RLS esconde os dos outros.
/// `ON CONFLICT DO NOTHING` não aborta a transação; colisão → novo código (até 5 tentativas, 07 §7.6).
internal sealed class InvitationWriter(AppDbContext db, ITenantContext tenant, IIdGenerator ids, IAuditWriter audit, TimeProvider time)
{
    public async Task<EmployeeInvitation> CreateAsync(string purpose, string[]? roles, Guid? targetEmployeeId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var expiresAt = now + (purpose == InvitationPurpose.Invite ? EmployeeInvitation.InviteLifetime : EmployeeInvitation.PasswordResetLifetime);
        var id = ids.New();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var code = InvitationCodeGenerator.New();
            var inserted = await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO employee_invitations (id, tenant_id, purpose, code, roles, target_employee_id, status, expires_at, created_by_employee_id, created_at)
                VALUES ({id}, {tenant.TenantId!.Value}, {purpose}, {code}, {roles}, {targetEmployeeId}, 'PENDING', {expiresAt}, {tenant.EmployeeId!.Value}, {now})
                ON CONFLICT (code) WHERE status = 'PENDING' DO NOTHING
                """,
                cancellationToken);
            if (inserted == 1)
            {
                audit.Add(purpose == InvitationPurpose.Invite ? "InvitationCreated" : "PasswordResetCodeIssued", "INVITATION", id,
                    after: new { purpose, roles, targetEmployeeId });
                return await db.Set<EmployeeInvitation>().AsNoTracking().SingleAsync(i => i.Id == id, cancellationToken);
            }
        }

        throw new InvalidOperationException("Não foi possível gerar um código de convite único.");
    }
}

/// DTOs de 06 §6.4.
internal sealed class ManagementDtos(AppDbContext db, IEmployeeDirectory employees, IOptions<InvitationOptions> options, TimeProvider time)
{
    public static TenantDto Tenant(Tenant t) =>
        new(t.Id, t.Version, t.Name, t.DocumentType, t.DocumentNumber, t.City, t.State, t.TimeZone, t.CreatedAt);

    public static TenantSettingsDto Settings(TenantSettings s) =>
        new(s.Version, s.KiloPricePerKg, s.DefaultDeliveryFee, s.EnabledPaymentMethods, s.SharedDeviceIdleSeconds);

    public async Task<IReadOnlyList<EmployeeDto>> EmployeesAsync(IReadOnlyList<TenantEmployee> list, CancellationToken cancellationToken)
    {
        var userIds = list.Select(e => e.UserId).ToArray();
        var contacts = await db.Set<User>().AsNoTracking().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new EmployeeContactDto(u.Email, u.PhoneNumber), cancellationToken);
        var names = await employees.GetDisplayNamesAsync(list.Where(e => e.DeactivatedByEmployeeId is not null).Select(e => e.DeactivatedByEmployeeId!.Value), cancellationToken);

        return list.Select(e => new EmployeeDto(
                e.Id, e.Version, e.UserId, e.DisplayName, e.Roles.Select(r => r.Role).Order().ToList(), e.Status, e.PinHash is not null,
                contacts.GetValueOrDefault(e.UserId, new EmployeeContactDto(null, null)), e.JoinedAt, e.DeactivatedAt,
                e.DeactivatedByEmployeeId is { } by ? new AuthorDto(by, names.GetValueOrDefault(by, string.Empty)) : null))
            .ToList();
    }

    public async Task<IReadOnlyList<InvitationDto>> InvitationsAsync(IReadOnlyList<EmployeeInvitation> list, CancellationToken cancellationToken)
    {
        var names = await employees.GetDisplayNamesAsync(
            list.Select(i => i.CreatedByEmployeeId).Concat(list.Where(i => i.TargetEmployeeId is not null).Select(i => i.TargetEmployeeId!.Value)),
            cancellationToken);
        var now = time.GetUtcNow();
        AuthorDto Author(Guid id) => new(id, names.GetValueOrDefault(id, string.Empty));

        return list.Select(i => new InvitationDto(
                i.Id, i.Purpose, i.Code, options.Value.LinkBaseUrl + i.Code, i.Roles,
                i.TargetEmployeeId is { } target ? Author(target) : null,
                i.EffectiveStatus(now), i.ExpiresAt, i.CreatedAt, Author(i.CreatedByEmployeeId), i.AcceptedAt))
            .ToList();
    }
}
