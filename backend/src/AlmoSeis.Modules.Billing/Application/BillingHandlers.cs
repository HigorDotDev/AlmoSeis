using System.Text.Json;
using AlmoSeis.Infrastructure.Audit;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Billing.Contracts;
using AlmoSeis.Modules.Billing.Domain;
using AlmoSeis.Modules.Billing.Infrastructure;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Modules.Billing.Application;

/// GET /subscription (06 §6.4): situação e prazos, sem preço nem link (ADR-0017).
internal sealed class GetSubscriptionQueryHandler(AppDbContext db, ITenantContext tenant, IOptions<BillingOptions> options, TimeProvider time)
    : IQueryHandler<GetSubscriptionQuery, SubscriptionDto>
{
    public async Task<SubscriptionDto> HandleAsync(GetSubscriptionQuery query, CancellationToken cancellationToken)
    {
        var s = await db.Set<Subscription>().AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId, cancellationToken)
            ?? throw new NotFoundException("SUBSCRIPTION");
        var now = time.GetUtcNow();
        var grace = s.GraceEndsAt(options.Value.GracePeriod);
        int? daysLeft = s.Status switch
        {
            SubscriptionStatus.Trialing => Days(s.TrialEndsAt - now),
            SubscriptionStatus.PastDue when grace is { } g => Days(g - now),
            _ => null,
        };

        var message = s.Status switch
        {
            SubscriptionStatus.Trialing => $"Teste grátis: {(daysLeft == 1 ? "falta 1 dia" : $"faltam {daysLeft} dias")}.",
            SubscriptionStatus.PastDue => "Pagamento pendente. Verifique o link de pagamento enviado ao seu e-mail ou WhatsApp.",
            SubscriptionStatus.Suspended => "Assinatura suspensa: o app está só para consulta. Verifique o link de pagamento enviado ao seu e-mail ou WhatsApp.",
            SubscriptionStatus.Canceled => "Assinatura cancelada. Fale com o suporte para exportar seus dados.",
            _ => null,
        };

        return new SubscriptionDto(s.Status, s.TrialEndsAt, s.CurrentPeriodEndsAt, s.PastDueSince, grace, daysLeft, s.CanWrite, message);
    }

    private static int Days(TimeSpan remaining) => Math.Max(0, (int)Math.Ceiling(remaining.TotalDays));
}

/// 07 §7.7: processa os webhooks pendentes por ordem de recebimento (`FOR UPDATE SKIP LOCKED`), de forma idempotente.
internal sealed class BillingWebhookProcessor(IServiceScopeFactory scopes, TimeProvider time, ILogger<BillingWebhookProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ProcessPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falha no processamento de webhooks de cobrança.");
            }
        }
    }

    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var recorder = scope.ServiceProvider.GetRequiredService<SubscriptionChangeRecorder>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var events = await db.Set<BillingWebhookEvent>()
            .FromSql($"SELECT * FROM billing_webhook_events WHERE processed_at IS NULL ORDER BY received_at LIMIT 50 FOR UPDATE SKIP LOCKED")
            .ToListAsync(cancellationToken);

        var now = time.GetUtcNow();
        foreach (var e in events)
        {
            await ApplyAsync(db, recorder, e, now, cancellationToken);
            e.ProcessedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return events.Count;
    }

    private async Task ApplyAsync(PlatformDbContext db, SubscriptionChangeRecorder recorder, BillingWebhookEvent e, DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(e.Payload);
        var root = doc.RootElement;
        var gatewaySubscriptionId = e.EventType.StartsWith("SUBSCRIPTION_", StringComparison.Ordinal)
            ? Text(root, "subscription", "id")
            : Text(root, "payment", "subscription");
        if (gatewaySubscriptionId is null)
        {
            logger.LogInformation("Webhook {EventId} ({EventType}) sem assinatura: ignorado.", e.GatewayEventId, e.EventType);
            return;
        }

        var subscription = await db.Set<Subscription>().SingleOrDefaultAsync(s => s.GatewaySubscriptionId == gatewaySubscriptionId, cancellationToken);
        if (subscription is null)
        {
            logger.LogWarning("Webhook {EventId}: assinatura {GatewaySubscriptionId} desconhecida.", e.GatewayEventId, gatewaySubscriptionId);
            return;
        }

        e.TenantId = subscription.TenantId;
        var before = subscription.Status;
        try
        {
            switch (e.EventType)
            {
                // Período pago: do vencimento da cobrança até um mês depois.
                case "PAYMENT_CONFIRMED" or "PAYMENT_RECEIVED":
                    var due = Text(root, "payment", "dueDate") is { } d && DateOnly.TryParse(d, out var dueDate)
                        ? new DateTimeOffset(dueDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                        : now;
                    subscription.Activate(due.AddMonths(1), now);
                    break;
                case "PAYMENT_OVERDUE":
                    subscription.MarkPastDue(now);
                    break;
                case "SUBSCRIPTION_DELETED" or "SUBSCRIPTION_INACTIVATED":
                    subscription.Cancel(now);
                    break;
                default:
                    return; // gravado e ignorado (06 §6.15)
            }
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Webhook {EventId} ignorado: {Reason}", e.GatewayEventId, ex.Message);
            return;
        }

        recorder.Record(db, subscription, AuditOrigins.BillingWebhook, e.EventType, before, now);
    }

    private static string? Text(JsonElement root, string obj, string property) =>
        root.TryGetProperty(obj, out var o) && o.ValueKind == JsonValueKind.Object && o.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}

/// 07 §7.11, de hora em hora com advisory lock: teste vencido e carência vencida → SUSPENDED; suspenso há 30 dias →
/// CANCELED; teste perto do fim → cria a assinatura no gateway, que envia o link de pagamento.
internal sealed class SubscriptionLifecycleJob(
    IServiceScopeFactory scopes,
    IOptions<BillingOptions> options,
    TimeProvider time,
    ILogger<SubscriptionLifecycleJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), time);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falha no job de ciclo de vida das assinaturas.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var recorder = scope.ServiceProvider.GetRequiredService<SubscriptionChangeRecorder>();
        var gateway = scope.ServiceProvider.GetRequiredService<ISubscriptionBillingGateway>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var locked = (await db.Database
            .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtextextended('job:subscription-lifecycle', 0)) AS \"Value\"")
            .ToListAsync(cancellationToken)).Single();
        if (!locked)
        {
            return false;
        }

        var o = options.Value;
        var now = time.GetUtcNow();
        var graceStart = now - o.GracePeriod;
        var cancelStart = now - TimeSpan.FromDays(o.SuspendedToCanceledDays);

        var due = await db.Set<Subscription>()
            .Where(s => (s.Status == SubscriptionStatus.Trialing && s.TrialEndsAt <= now)
                || (s.Status == SubscriptionStatus.PastDue && s.PastDueSince <= graceStart)
                || (s.Status == SubscriptionStatus.Suspended && s.SuspendedAt <= cancelStart))
            .ToListAsync(cancellationToken);

        foreach (var subscription in due)
        {
            var before = subscription.Status;
            var reason = before switch
            {
                SubscriptionStatus.Trialing => "TRIAL_ENDED",
                SubscriptionStatus.PastDue => "GRACE_ENDED",
                _ => "SUSPENDED_TOO_LONG",
            };
            _ = before == SubscriptionStatus.Suspended ? subscription.Cancel(now) : subscription.Suspend(now);
            recorder.Record(db, subscription, AuditOrigins.System, reason, before, now);
        }

        if (gateway.IsConfigured)
        {
            await CreateGatewaySubscriptionsAsync(db, gateway, now, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task CreateGatewaySubscriptionsAsync(PlatformDbContext db, ISubscriptionBillingGateway gateway, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var noticeStart = now + TimeSpan.FromDays(options.Value.TrialEndingNoticeDays);
        var ending = await db.Set<Subscription>()
            .Where(s => s.Status == SubscriptionStatus.Trialing && s.TrialEndsAt <= noticeStart && s.GatewaySubscriptionId == null)
            .ToListAsync(cancellationToken);

        foreach (var subscription in ending)
        {
            // Dados do proprietário: o job é de plataforma e lê as tabelas de identidade por SQL.
            var customer = (await db.Database.SqlQuery<OwnerRow>(
                $"""
                SELECT t.name, t.document_number, u.email, u.phone_number
                  FROM tenants t
                  JOIN tenant_employees e ON e.tenant_id = t.id AND e.status = 'ACTIVE'
                  JOIN tenant_employee_roles r ON r.tenant_id = e.tenant_id AND r.tenant_employee_id = e.id AND r.role = 'OWNER'
                  JOIN users u ON u.id = e.user_id
                 WHERE t.id = {subscription.TenantId}
                 ORDER BY e.joined_at
                 LIMIT 1
                """).ToListAsync(cancellationToken)).SingleOrDefault();
            if (customer is null)
            {
                continue;
            }

            try
            {
                var ids = await gateway.CreateCustomerAndSubscriptionAsync(
                    new BillingCustomer(subscription.TenantId, customer.Name, customer.DocumentNumber, customer.Email, customer.PhoneNumber,
                        DateOnly.FromDateTime(subscription.TrialEndsAt.UtcDateTime)),
                    subscription.MonthlyPrice,
                    cancellationToken);
                subscription.LinkGateway(ids.CustomerId, ids.SubscriptionId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Uma falha de gateway não impede as outras; a próxima execução tenta de novo.
                logger.LogError(ex, "Falha ao criar a assinatura no gateway para o tenant {TenantId}.", subscription.TenantId);
            }
        }
    }

    private sealed record OwnerRow(string Name, string DocumentNumber, string? Email, string? PhoneNumber);
}
