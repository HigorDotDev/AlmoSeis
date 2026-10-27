using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;

namespace AlmoSeis.Modules.Identity.Contracts;

// ---------- Estabelecimento e configurações (06 §6.4) ----------

public sealed record TenantDto(
    Guid Id, int Version, string Name, string DocumentType, string DocumentNumber, string City, string State, string TimeZone, DateTimeOffset CreatedAt)
    : IVersionedDto;

public sealed record UpdateTenantRequest(Optional<string> Name, Optional<string> City, Optional<string> State, Optional<string> TimeZone);

public sealed record UpdateTenantCommand(UpdateTenantRequest Changes);

public sealed record GetTenantQuery;

public sealed record TenantSettingsDto(
    int Version, Money? KiloPricePerKg, Money DefaultDeliveryFee, IReadOnlyList<string> EnabledPaymentMethods, int SharedDeviceIdleSeconds)
    : IVersionedDto;

public sealed record UpdateTenantSettingsRequest(
    Optional<Money?> KiloPricePerKg,
    Optional<Money> DefaultDeliveryFee,
    Optional<IReadOnlyList<string>> EnabledPaymentMethods,
    Optional<int> SharedDeviceIdleSeconds);

public sealed record UpdateTenantSettingsCommand(UpdateTenantSettingsRequest Changes);

public sealed record GetTenantSettingsQuery;

// ---------- Funcionários ----------

public sealed record EmployeeContactDto(string? Email, string? PhoneNumber);

public sealed record EmployeeDto(
    Guid Id,
    int Version,
    Guid UserId,
    string DisplayName,
    IReadOnlyList<string> Roles,
    string Status,
    bool HasPin,
    EmployeeContactDto Contact,
    DateTimeOffset JoinedAt,
    DateTimeOffset? DeactivatedAt,
    AuthorDto? DeactivatedBy) : IVersionedDto;

public sealed record ListEmployeesQuery(string? Status);

public sealed record GetEmployeeQuery(Guid EmployeeId);

public sealed record UpdateEmployeeRequest(Optional<string> DisplayName, Optional<IReadOnlyList<string>> Roles);

public sealed record UpdateEmployeeCommand(Guid EmployeeId, UpdateEmployeeRequest Changes);

public sealed record DeactivateEmployeeCommand(Guid EmployeeId);

public sealed record ReactivateEmployeeCommand(Guid EmployeeId);

public sealed record IssuePasswordResetCodeCommand(Guid EmployeeId);

// ---------- Convites (ADR-0018) ----------

public sealed record InvitationDto(
    Guid Id,
    string Purpose,
    string Code,
    string Link,
    IReadOnlyList<string>? Roles,
    AuthorDto? TargetEmployee,
    string Status,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt,
    AuthorDto CreatedBy,
    DateTimeOffset? AcceptedAt);

public sealed record CreateInvitationCommand(IReadOnlyList<string> Roles);

public sealed record ListInvitationsQuery(string? Status);

public sealed record RevokeInvitationCommand(Guid InvitationId);

/// GET /invitations/{code} (público): o que o convidado vê antes de aceitar.
public sealed record PublicInvitationDto(string Code, string Purpose, string TenantName, IReadOnlyList<string> Roles, string InvitedBy, DateTimeOffset ExpiresAt);

public sealed record GetInvitationByCodeQuery(string Code);

public sealed record AcceptInvitationRequest(NewAccountInput? NewAccount, ExistingAccountInput? ExistingAccount, string DisplayName, string InstallationId);

public sealed record AcceptInvitationCommand(string Code, AcceptInvitationRequest Request);

// ---------- Senha e PIN ----------

public sealed record SetOwnPinCommand(string Pin, string CurrentPassword);

/// POST /auth/password/reset: `code` + `login` (código emitido por quem gerencia funcionários).
public sealed record ResetPasswordCommand(string? Code, string? Login, string? EmailToken, string NewPassword);

// ---------- Portas ----------

/// Implementado pela Api (cache de acesso do token): mudanças de perfil ou status valem em até 30 s (ADR-0027).
public interface IEmployeeAccessInvalidator
{
    void Invalidate(Guid employeeId);
}
