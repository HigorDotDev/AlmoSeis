using AlmoSeis.SharedKernel.Domain;

namespace AlmoSeis.Modules.Ordering.Domain;

/// Token de acompanhamento (ADR-0047 itens 1 a 4): só o SHA-256 é guardado; o token em claro aparece uma vez, na resposta
/// do envio. Tabela de plataforma (`order_tracking_tokens`): leitura aberta para a resolução, escrita só no tenant.
internal sealed class OrderTrackingToken : ITenantOwned
{
    /// ADR-0047 item 4.
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private OrderTrackingToken()
    {
    }

    public string TokenHash { get; private set; } = null!;
    public Guid TenantId { get; private set; }
    public Guid OrderId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }

    public static OrderTrackingToken Issue(string tokenHash, Guid tenantId, Guid orderId, DateTimeOffset now) => new()
    {
        TokenHash = tokenHash,
        TenantId = tenantId,
        OrderId = orderId,
        CreatedAt = now,
        ExpiresAt = now + Lifetime,
    };
}

/// Entrada do pedido da página (ADR-0045 item 14): hash do IP com sal diário, para o limite por IP e a investigação de
/// trote. Apagada em 7 dias (ADR-0052 item 5).
internal sealed class WebOrderIntake : ITenantOwned
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private WebOrderIntake()
    {
    }

    public Guid OrderId { get; private set; }
    public Guid TenantId { get; private set; }
    public string IpHash { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }

    public static WebOrderIntake Record(Guid orderId, Guid tenantId, string ipHash, DateTimeOffset now) =>
        new() { OrderId = orderId, TenantId = tenantId, IpHash = ipHash, CreatedAt = now };
}
