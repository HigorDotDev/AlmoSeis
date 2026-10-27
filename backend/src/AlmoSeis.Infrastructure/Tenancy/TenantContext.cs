namespace AlmoSeis.Infrastructure.Tenancy;

/// Quem está chamando, por requisição (scoped). Vem só das claims do JWT (ADR-0026).
public interface ITenantContext
{
    Guid? TenantId { get; }

    Guid? UserId { get; }

    Guid? EmployeeId { get; }

    Guid? SharedDeviceId { get; }

    string? InstallationId { get; }

    /// Só o PlatformDbContext liga isto (ADR-0025).
    bool BypassTenantFilter { get; }

    bool IsInitialized { get; }

    /// Requisição autenticada: valores das claims do access token.
    void Initialize(Guid tenantId, Guid userId, Guid employeeId, Guid? sharedDeviceId, string? installationId);

    /// Login: só o usuário, sem tenant ativo (05 §5.12, grupo 2).
    void InitializeUserOnly(Guid userId);

    /// Autocadastro, login, refresh e aceite de convite: o próprio fluxo define o tenant antes de gravar.
    void InitializeForTenant(Guid tenantId, Guid userId);

    /// Única mutação permitida depois de inicializado: POST /auth/switch-tenant.
    void SwitchTenant(Guid tenantId, Guid employeeId);
}

public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }

    public Guid? UserId { get; private set; }

    public Guid? EmployeeId { get; private set; }

    public Guid? SharedDeviceId { get; private set; }

    public string? InstallationId { get; private set; }

    public bool BypassTenantFilter { get; init; }

    public bool IsInitialized { get; private set; }

    public void Initialize(Guid tenantId, Guid userId, Guid employeeId, Guid? sharedDeviceId, string? installationId)
    {
        EnsureNotInitialized();
        (TenantId, UserId, EmployeeId, SharedDeviceId, InstallationId, IsInitialized) =
            (tenantId, userId, employeeId, sharedDeviceId, installationId, true);
    }

    public void InitializeUserOnly(Guid userId)
    {
        EnsureNotInitialized();
        (UserId, IsInitialized) = (userId, true);
    }

    public void InitializeForTenant(Guid tenantId, Guid userId)
    {
        // Permitido depois de InitializeUserOnly (login escolhe o tenant depois de listar os vínculos).
        if (TenantId is not null)
        {
            throw new InvalidOperationException("O tenant da requisição já foi definido.");
        }

        (TenantId, UserId, IsInitialized) = (tenantId, userId, true);
    }

    public void SwitchTenant(Guid tenantId, Guid employeeId)
    {
        if (!IsInitialized || UserId is null)
        {
            throw new InvalidOperationException("Troca de tenant exige uma sessão autenticada.");
        }

        (TenantId, EmployeeId, SharedDeviceId) = (tenantId, employeeId, null);
    }

    private void EnsureNotInitialized()
    {
        if (IsInitialized)
        {
            throw new InvalidOperationException("O contexto de tenant é imutável depois de inicializado.");
        }
    }
}
