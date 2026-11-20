namespace AlmoSeis.Infrastructure.Tenancy;

/// De onde veio o tenant da requisição (ADR-0045 item 3). Imutável depois de definido.
public enum TenantSource
{
    None,

    /// Claims do access token, inclusive os fluxos de autenticação que escolhem o tenant (ADR-0026).
    Jwt,

    /// Rota pública da página: `{slug}` resolvido em `store_slugs` (ADR-0045 item 4).
    PublicSlug,

    /// Rota pública de acompanhamento: hash do token em `order_tracking_tokens` (ADR-0047).
    TrackingToken,

    /// Webhook do provedor de pagamento (Fase 2, ADR-0050).
    GatewayWebhook,

    /// Jobs do sistema (expiração, retenção).
    System,
}

/// Quem está chamando, por requisição (scoped). Nas rotas autenticadas vem só das claims do JWT (ADR-0026); nas rotas
/// públicas, do slug ou do token resolvidos no servidor (ADR-0045).
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

    TenantSource Source { get; }

    /// Rota pública ou de acompanhamento sem usuário (ADR-0045 itens 3 e 6): só o tenant resolvido no servidor.
    /// `app.user_id` fica vazio, e a auditoria sai sem ator.
    bool IsAnonymous => Source is TenantSource.PublicSlug or TenantSource.TrackingToken;

    /// Requisição autenticada: valores das claims do access token.
    void Initialize(Guid tenantId, Guid userId, Guid employeeId, Guid? sharedDeviceId, string? installationId);

    /// Login: só o usuário, sem tenant ativo (05 §5.12, grupo 2).
    void InitializeUserOnly(Guid userId);

    /// Autocadastro, login, refresh e aceite de convite: o próprio fluxo define o tenant antes de gravar.
    void InitializeForTenant(Guid tenantId, Guid userId);

    /// Jobs do sistema e o canal web (ADR-0046): só o tenant, sem usuário nem funcionário. A auditoria sai sem ator.
    void InitializeForSystem(Guid tenantId);

    /// Rotas `/api/v1/public/*` (ADR-0045): o tenant resolvido pelo slug ou pelo token de acompanhamento.
    void InitializeForPublic(Guid tenantId, TenantSource source);

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

    public TenantSource Source { get; private set; }

    public void Initialize(Guid tenantId, Guid userId, Guid employeeId, Guid? sharedDeviceId, string? installationId)
    {
        EnsureNotInitialized();
        (TenantId, UserId, EmployeeId, SharedDeviceId, InstallationId, IsInitialized, Source) =
            (tenantId, userId, employeeId, sharedDeviceId, installationId, true, TenantSource.Jwt);
    }

    public void InitializeUserOnly(Guid userId)
    {
        EnsureNotInitialized();
        (UserId, IsInitialized, Source) = (userId, true, TenantSource.Jwt);
    }

    public void InitializeForTenant(Guid tenantId, Guid userId)
    {
        // Permitido depois de InitializeUserOnly (login escolhe o tenant depois de listar os vínculos).
        if (TenantId is not null)
        {
            throw new InvalidOperationException("O tenant da requisição já foi definido.");
        }

        (TenantId, UserId, IsInitialized, Source) = (tenantId, userId, true, TenantSource.Jwt);
    }

    public void InitializeForSystem(Guid tenantId)
    {
        EnsureNotInitialized();
        (TenantId, IsInitialized, Source) = (tenantId, true, TenantSource.System);
    }

    public void InitializeForPublic(Guid tenantId, TenantSource source)
    {
        EnsureNotInitialized();
        if (source is not (TenantSource.PublicSlug or TenantSource.TrackingToken))
        {
            throw new ArgumentOutOfRangeException(nameof(source), "Rota pública resolve o tenant só pelo slug ou pelo token.");
        }

        (TenantId, IsInitialized, Source) = (tenantId, true, source);
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
