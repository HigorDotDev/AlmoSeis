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

/// Na web (ADR-0053) o refresh vem do cookie: o corpo traz só `installationId` e a Api preenche `RefreshToken`.
public sealed record RefreshTokenCommand(string? RefreshToken, string InstallationId);

/// Na web, o corpo é vazio e o refresh vem do cookie.
public sealed record LogoutCommand(string? RefreshToken);

public sealed record SwitchTenantCommand(Guid TenantId);

public sealed record GetMeQuery;

public sealed record ListRolesQuery;

// ===== Respostas =====

public sealed record TokensDto(
    string TokenType,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string? RefreshToken, // null na web: vai no cookie (ADR-0053)
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

/// POST /auth/admin/login (ADR-0043).
public sealed record AdminLoginCommand(string Login, string Password);

public sealed record AdminUserDto(Guid Id, string FullName, string? Email);

public sealed record AdminSessionDto(string AccessToken, DateTimeOffset ExpiresAt, AdminUserDto User);

/// Revalida o token de admin a cada requisição: ainda é admin e com o mesmo security stamp (ADR-0043 §4).
public interface IPlatformAdminCheck
{
    Task<bool> IsValidAsync(Guid userId, string securityStamp, CancellationToken cancellationToken);
}

/// Nome de exibição dos autores ({ id, displayName }) para os DTOs dos outros módulos.
public interface IEmployeeDirectory
{
    Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesAsync(IEnumerable<Guid> employeeIds, CancellationToken cancellationToken);

    /// Nome dos aparelhos compartilhados (auditoria: `sharedDevice`).
    Task<IReadOnlyDictionary<Guid, string>> GetDeviceNamesAsync(IEnumerable<Guid> deviceIds, CancellationToken cancellationToken);

    /// Vínculos ativos do usuário da sessão em todos os estabelecimentos (RLS de vínculo, 05 §5.12 grupo 2). Usado pelo
    /// push para pôr o nome do estabelecimento no título quando há mais de um (ADR-0049 item 3).
    Task<int> CountActiveMembershipsAsync(Guid userId, CancellationToken cancellationToken);
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

/// Estabelecimento recém-criado no autocadastro (ADR-0018).
public sealed record NewTenantInfo(Guid TenantId, string Name, string City, DateTimeOffset CreatedAt);

/// Declarada aqui e implementada pelos módulos acima do Identity que preparam o tenant novo na **mesma transação** do
/// autocadastro (ex.: a página de pedidos nasce inativa com o slug reservado, ADR-0044 item 11). Sessão já no tenant.
public interface ITenantCreatedListener
{
    Task OnTenantCreatedAsync(NewTenantInfo tenant, CancellationToken cancellationToken);
}

/// Fim de sessão ou de acesso de um aparelho (ADR-0049 item 4). Declarada aqui e implementada pelos módulos acima do
/// Identity que guardam algo por instalação (o registro de push). Chamada na **mesma transação**, com a sessão no tenant
/// afetado.
public interface IDeviceAccessListener
{
    /// Logout sem aparelho compartilhado e troca de estabelecimento: a instalação sai do tenant da sessão. O logout de um
    /// aparelho compartilhado (inclusive pelo tempo ocioso) não chama: o tablet continua avisando (ADR-0049 item 5).
    Task OnInstallationSignedOutAsync(string installationId, CancellationToken cancellationToken);

    /// Desativação e troca de perfis (`AccessRevoked`).
    Task OnEmployeeAccessRevokedAsync(Guid employeeId, CancellationToken cancellationToken);

    /// Revogação do aparelho compartilhado (ou novo registro da mesma instalação).
    Task OnSharedDevicesRevokedAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken cancellationToken);
}
