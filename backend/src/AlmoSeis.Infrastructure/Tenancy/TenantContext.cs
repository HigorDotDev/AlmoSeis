namespace AlmoSeis.Infrastructure.Tenancy;

/// Quem está chamando, por requisição (scoped). Vem só das claims do JWT (ADR-0026).
public interface ITenantContext
{
    Guid? TenantId { get; }

    Guid? UserId { get; }

    Guid? EmployeeId { get; }

    Guid? SharedDeviceId { get; }

    /// Só o PlatformDbContext liga isto (ADR-0025).
    bool BypassTenantFilter { get; }

    bool IsInitialized { get; }

    void Initialize(Guid tenantId, Guid userId, Guid employeeId, Guid? sharedDeviceId);

    /// Login e troca de tenant: só o usuário, sem tenant ativo (05 §5.12, grupo 2).
    void InitializeUserOnly(Guid userId);

    /// Autocadastro e aceite de convite: tenant definido pelo próprio fluxo antes de gravar.
    void InitializeForTenant(Guid tenantId, Guid userId);
}

public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }

    public Guid? UserId { get; private set; }

    public Guid? EmployeeId { get; private set; }

    public Guid? SharedDeviceId { get; private set; }

    public bool BypassTenantFilter { get; init; }

    public bool IsInitialized { get; private set; }

    public void Initialize(Guid tenantId, Guid userId, Guid employeeId, Guid? sharedDeviceId)
    {
        EnsureNotInitialized();
        (TenantId, UserId, EmployeeId, SharedDeviceId, IsInitialized) = (tenantId, userId, employeeId, sharedDeviceId, true);
    }

    public void InitializeUserOnly(Guid userId)
    {
        EnsureNotInitialized();
        (UserId, IsInitialized) = (userId, true);
    }

    public void InitializeForTenant(Guid tenantId, Guid userId)
    {
        // Permitido depois de InitializeUserOnly (aceite de convite por usuário já logado no app).
        if (IsInitialized && TenantId is not null)
        {
            throw new InvalidOperationException("O tenant da requisição já foi definido.");
        }

        (TenantId, UserId, IsInitialized) = (tenantId, userId, true);
    }

    private void EnsureNotInitialized()
    {
        if (IsInitialized)
        {
            throw new InvalidOperationException("O contexto de tenant é imutável depois de inicializado.");
        }
    }
}
