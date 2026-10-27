using AlmoSeis.SharedKernel.Domain;

namespace AlmoSeis.Modules.Identity.Contracts;

// ===== Comandos e consultas (corpo das requisições de 06 §6.3) =====

public sealed record NewAccountInput(string FullName, string? Email, string? PhoneNumber, string Password);

public sealed record ExistingAccountInput(string Login, string Password);

public sealed record TenantInput(string Name, string DocumentType, string DocumentNumber, string City, string State, string? TimeZone);

public sealed record SignUpCommand(
    NewAccountInput? NewAccount,
    ExistingAccountInput? ExistingAccount,
    TenantInput Tenant,
    string AcceptedTermsVersion,
    string InstallationId);

public sealed record LoginCommand(string Login, string Password, string InstallationId, Guid? PreferredTenantId);

public sealed record RefreshTokenCommand(string RefreshToken, string InstallationId);

public sealed record LogoutCommand(string RefreshToken);

public sealed record SwitchTenantCommand(Guid TenantId);

public sealed record GetMeQuery;

public sealed record ListRolesQuery;

// ===== Respostas =====

public sealed record TokensDto(
    string TokenType,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);

public sealed record MeUserDto(Guid Id, string FullName, string? Email, string? PhoneNumber);

public sealed record MeTenantDto(Guid Id, string Name, string TimeZone);

public sealed record MeEmployeeDto(Guid Id, string DisplayName, IReadOnlyList<string> Roles, bool HasPin);

public sealed record MembershipDto(Guid TenantId, string TenantName, Guid EmployeeId, IReadOnlyList<string> Roles);

public sealed record MeSubscriptionDto(string? Status, DateTimeOffset? TrialEndsAt, int? DaysLeft, bool CanWrite);

public sealed record MeSharedDeviceDto(Guid Id, string Name);

public sealed record MeDto(
    MeUserDto User,
    MeTenantDto Tenant,
    MeEmployeeDto Employee,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<MembershipDto> Memberships,
    MeSubscriptionDto Subscription,
    MeSharedDeviceDto? SharedDevice,
    DateTimeOffset ServerTime);

public sealed record AuthResultDto(TokensDto Tokens, MeDto Me);

/// Login/refresh podem falhar e ainda assim precisar gravar (contador de tentativas, revogação da
/// família). O handler devolve a falha; o controller a converte em erro DEPOIS do commit.
public sealed record AuthOutcome<T>(T? Value, string? FailureCode, IReadOnlyDictionary<string, object?>? FailureExtensions = null)
    where T : class
{
    public static AuthOutcome<T> Success(T value) => new(value, null);

    public static AuthOutcome<T> Failure(string errorCode, IReadOnlyDictionary<string, object?>? extensions = null) =>
        new(null, errorCode, extensions);
}

public sealed record RoleDto(string Role, string Name, IReadOnlyList<string> Permissions);

// ===== Ports =====

public sealed record EmployeeAccess(Guid EmployeeId, Guid TenantId, bool IsActive, Guid SecurityStamp, IReadOnlyList<string> Roles, IReadOnlySet<string> Permissions);

/// Usado pela validação do token na Api (07 §7.4): status, stamp e permissões do funcionário.
public interface IEmployeeAccessReader
{
    Task<EmployeeAccess?> GetAsync(Guid employeeId, CancellationToken cancellationToken);
}

/// Nome de exibição dos autores ({ id, displayName }) para os DTOs dos outros módulos.
public interface IEmployeeDirectory
{
    Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesAsync(IEnumerable<Guid> employeeIds, CancellationToken cancellationToken);
}

/// Data local do estabelecimento (fuso do tenant): a `BusinessDate` da abertura do expediente (ADR-0019).
public interface ITenantCalendar
{
    Task<DateOnly> TodayAsync(CancellationToken cancellationToken);
}

/// Configuração operacional do tenant corrente (06 §6.4): preço do quilo, taxa padrão e formas de pagamento.
public sealed record TenantOperationalSettings(Money? KiloPricePerKg, Money DefaultDeliveryFee, IReadOnlyList<string> EnabledPaymentMethods);

public interface ITenantSettingsReader
{
    Task<TenantOperationalSettings> GetAsync(CancellationToken cancellationToken);
}
