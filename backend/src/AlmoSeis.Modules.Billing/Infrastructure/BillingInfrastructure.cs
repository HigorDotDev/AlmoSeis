using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Persistence.Entities;
using AlmoSeis.Infrastructure.Security;
using AlmoSeis.Modules.Billing.Contracts;
using AlmoSeis.Modules.Billing.Domain;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Modules.Billing.Infrastructure;

public sealed class BillingOptions
{
    public const string Section = "Billing";

    public int GracePeriodDays { get; set; } = 5;

    public int SuspendedToCanceledDays { get; set; } = 30;

    /// A assinatura no gateway é criada quando faltam estes dias para o fim do teste (o Asaas envia o link).
    public int TrialEndingNoticeDays { get; set; } = 2;

    public string PlanCode { get; set; } = "PILOT";

    public decimal MonthlyPrice { get; set; }

    public AsaasOptions Asaas { get; set; } = new();

    public TimeSpan GracePeriod => TimeSpan.FromDays(GracePeriodDays);
}

public sealed class AsaasOptions
{
    public string BaseUrl { get; set; } = "https://api.asaas.com/v3";

    public string? ApiKey { get; set; }

    public string? WebhookToken { get; set; }
}

/// billing_webhook_events (05 §5.4): sem RLS, só o papel de plataforma acessa.
internal sealed class BillingWebhookEvent
{
    public Guid Id { get; init; }
    public required string Gateway { get; init; }
    public required string GatewayEventId { get; init; }
    public required string EventType { get; init; }
    public Guid? TenantId { get; set; }
    public required string Payload { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
    public DateTimeOffset? ProcessedAt { get; set; }
}

internal sealed class BillingPort(AppDbContext db, IIdGenerator ids, ISecretHasher hasher, IOptions<BillingOptions> options, TimeProvider time) : IBillingPort
{
    public async Task StartTrialAsync(Guid tenantId, IReadOnlyCollection<string> claimSources, CancellationToken cancellationToken)
    {
        var keys = claimSources.Select(hasher.Hash).Distinct().ToList();
        if (await db.Set<TrialClaim>().AnyAsync(c => keys.Contains(c.ClaimKey), cancellationToken))
        {
            throw new BusinessException(ErrorCodes.TrialAlreadyClaimed);
        }

        var now = time.GetUtcNow();
        foreach (var key in keys)
        {
            db.Add(TrialClaim.Create(key, tenantId, now));
        }

        db.Add(Subscription.StartTrial(ids.New(), tenantId, options.Value.PlanCode, Money.Of(options.Value.MonthlyPrice), now));
    }

    public async Task<SubscriptionSummary?> GetSummaryAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        // No autocadastro a assinatura ainda está só no change tracker (o UnitOfWork grava no commit).
        var subscription = db.Set<Subscription>().Local.FirstOrDefault(s => s.TenantId == tenantId)
            ?? await db.Set<Subscription>().AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);
        return subscription is null
            ? null
            : new SubscriptionSummary(subscription.Status, subscription.TrialEndsAt, subscription.DaysLeft(time.GetUtcNow()), subscription.CanWrite);
    }
}

/// Cache de 60 s por tenant (07 §7.4). Invalidado no mesmo processo por webhook e job; outras réplicas
/// enxergam a mudança em até 60 s.
internal sealed class SubscriptionAccessCache(IMemoryCache cache, IServiceProvider services) : ISubscriptionAccess
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    public async Task<SubscriptionAccessState> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await cache.GetOrCreateAsync(Key(tenantId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Ttl;
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var status = await db.Set<Subscription>().AsNoTracking()
                .Where(s => s.TenantId == tenantId)
                .Select(s => s.Status)
                .SingleOrDefaultAsync(cancellationToken);

            // Tenant sem assinatura não existe fora de dados corrompidos: trata como sem escrita.
            return status is null
                ? new SubscriptionAccessState(SubscriptionStatus.Canceled, false)
                : new SubscriptionAccessState(status, status is not (SubscriptionStatus.Suspended or SubscriptionStatus.Canceled));
        }))!;

    public void Invalidate(Guid tenantId) => cache.Remove(Key(tenantId));

    private static string Key(Guid tenantId) => $"subscription-access:{tenantId}";
}

/// Grava a mudança de assinatura feita por webhook ou job (contexto de plataforma, sem UnitOfWork):
/// auditoria, evento `SubscriptionStatusChanged` no outbox (sem `seq`: a assinatura não entra no feed) e cache.
internal sealed class SubscriptionChangeRecorder(IIdGenerator ids, ISubscriptionAccess access)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Record(PlatformDbContext db, Subscription subscription, string origin, string reason, string before, DateTimeOffset now, Guid? actorUserId = null)
    {
        foreach (var change in subscription.DomainEvents.OfType<SubscriptionStatusChanged>())
        {
            db.Add(new AuditLog
            {
                Id = ids.New(),
                TenantId = change.TenantId,
                Origin = origin,
                ActorUserId = actorUserId,
                OperationType = "SubscriptionStatusChanged",
                EntityType = "SUBSCRIPTION",
                EntityId = change.SubscriptionId,
                Before = JsonSerializer.Serialize(new { status = before }, Json),
                After = JsonSerializer.Serialize(new { status = change.Status, reason }, Json),
                CorrelationId = ids.New().ToString("N"),
                OccurredAt = now,
            });

            var eventId = ids.New();
            db.Add(new OutboxMessage
            {
                Id = eventId,
                TenantId = change.TenantId,
                EventType = "SubscriptionStatusChanged",
                OccurredAt = change.OccurredAt,
                Payload = JsonSerializer.Serialize(
                    new
                    {
                        eventId,
                        eventType = "SubscriptionStatusChanged",
                        seq = (long?)null,
                        occurredAt = change.OccurredAt,
                        entityType = "SUBSCRIPTION",
                        entityId = change.SubscriptionId,
                        entityVersion = subscription.Version,
                        actorEmployeeId = (Guid?)null,
                        data = new { status = change.Status, canWrite = change.CanWrite },
                    },
                    Json),
            });
            access.Invalidate(change.TenantId);
        }

        subscription.ClearDomainEvents();
    }
}

/// POST /webhooks/asaas (06 §6.15). Token comparado em tempo constante; gravação idempotente por id do evento.
internal sealed class AsaasWebhookReceiver(PlatformDbContext db, IIdGenerator ids, IOptions<BillingOptions> options, TimeProvider time)
    : IAsaasWebhookReceiver
{
    public const string Gateway = "ASAAS";

    public async Task<WebhookReceipt> ReceiveAsync(string? accessToken, string payload, CancellationToken cancellationToken)
    {
        var expected = options.Value.Asaas.WebhookToken;
        if (string.IsNullOrEmpty(expected) || accessToken is null
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(accessToken), Encoding.UTF8.GetBytes(expected)))
        {
            return new WebhookReceipt(false);
        }

        string eventId, eventType;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            eventId = doc.RootElement.GetProperty("id").GetString()!;
            eventType = doc.RootElement.GetProperty("event").GetString()!;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new BusinessException(ErrorCodes.MalformedRequest, "Corpo do webhook sem `id` ou `event`.");
        }

        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO billing_webhook_events (id, gateway, gateway_event_id, event_type, payload, received_at)
            VALUES ({ids.New()}, {Gateway}, {eventId}, {eventType}, {payload}::jsonb, {time.GetUtcNow()})
            ON CONFLICT (gateway, gateway_event_id) DO NOTHING
            """,
            cancellationToken);
        return new WebhookReceipt(true);
    }
}

/// Dados do proprietário para criar cliente e assinatura no gateway.
internal sealed record BillingCustomer(Guid TenantId, string Name, string DocumentNumber, string? Email, string? Phone, DateOnly FirstDueDate);

internal sealed record GatewayIds(string CustomerId, string SubscriptionId);

/// ADR-0017 §1: o gateway fica atrás desta interface.
internal interface ISubscriptionBillingGateway
{
    bool IsConfigured { get; }

    Task<GatewayIds> CreateCustomerAndSubscriptionAsync(BillingCustomer customer, Money monthlyPrice, CancellationToken cancellationToken);
}

/// Asaas v3: cliente + assinatura mensal com `billingType UNDEFINED` (o cliente escolhe Pix, boleto ou cartão
/// no link enviado pelo próprio Asaas). `externalReference` = id do tenant.
internal sealed class AsaasGateway(HttpClient http, IOptions<BillingOptions> options, ILogger<AsaasGateway> logger) : ISubscriptionBillingGateway
{
    public bool IsConfigured => !string.IsNullOrEmpty(options.Value.Asaas.ApiKey);

    public async Task<GatewayIds> CreateCustomerAndSubscriptionAsync(BillingCustomer customer, Money monthlyPrice, CancellationToken cancellationToken)
    {
        var asaas = options.Value.Asaas;
        var customerId = await PostAsync(asaas, "customers", new
        {
            name = customer.Name,
            cpfCnpj = customer.DocumentNumber,
            email = customer.Email,
            mobilePhone = customer.Phone?.TrimStart('+'),
            externalReference = customer.TenantId,
        }, cancellationToken);

        var subscriptionId = await PostAsync(asaas, "subscriptions", new
        {
            customer = customerId,
            billingType = "UNDEFINED",
            value = monthlyPrice.Amount,
            nextDueDate = customer.FirstDueDate.ToString("yyyy-MM-dd"),
            cycle = "MONTHLY",
            description = "AlmoSeis",
            externalReference = customer.TenantId,
        }, cancellationToken);

        logger.LogInformation("Assinatura {SubscriptionId} criada no Asaas para o tenant {TenantId}.", subscriptionId, customer.TenantId);
        return new GatewayIds(customerId, subscriptionId);
    }

    private async Task<string> PostAsync(AsaasOptions asaas, string path, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{asaas.BaseUrl.TrimEnd('/')}/{path}") { Content = JsonContent.Create(body) };
        request.Headers.Add("access_token", asaas.ApiKey);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return json.GetProperty("id").GetString()!;
    }
}
