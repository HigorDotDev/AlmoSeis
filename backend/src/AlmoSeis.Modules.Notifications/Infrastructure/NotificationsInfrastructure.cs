using System.Text.Json;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Realtime;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Notifications.Application;
using AlmoSeis.Modules.Notifications.Contracts;
using AlmoSeis.Modules.Notifications.Domain;
using AlmoSeis.Modules.Ordering.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Modules.Notifications.Infrastructure;

/// Mapeamento de 05 §5.17 (0006).
internal sealed class NotificationsModelConfiguration : IModuleModelConfiguration
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PushRegistration>(b =>
        {
            b.ToTable("push_registrations");
            b.HasKey(x => x.Id);
            b.Property(x => x.InstallationId).HasMaxLength(64);
            b.Property(x => x.ExpoPushToken).HasMaxLength(PushRegistration.TokenMax);
        });

        modelBuilder.Entity<PushDelivery>(b =>
        {
            b.ToTable("push_deliveries");
            b.HasKey(x => new { x.EventId, x.RegistrationId });
            b.Property(x => x.TicketId).HasMaxLength(64);
            b.Property(x => x.Error).HasMaxLength(PushDelivery.ErrorMax);
            b.Property(x => x.ReceiptError).HasMaxLength(PushDelivery.ErrorMax);
        });
    }
}

/// Entrega a mensagem `WebOrderPushRequested` do outbox (ADR-0049 item 8) num escopo do tenant da mensagem
/// (`InitializeForSystem`: RLS e filtro do EF). 3 tentativas com 30 s de espera; depois desiste (sai da fila com o erro).
internal sealed class WebOrderPushConsumer(IServiceScopeFactory scopes, IOptions<PushOptions> options) : IOutboxConsumer
{
    public string EventType => WebOrderPushMessage.EventType;

    public int? MaxAttempts => options.Value.MaxAttempts;

    public TimeSpan RetryDelay => TimeSpan.FromSeconds(options.Value.RetryDelaySeconds);

    public async Task HandleAsync(Guid tenantId, JsonElement envelope, CancellationToken cancellationToken)
    {
        var data = envelope.GetProperty("data");
        var request = new WebOrderPushRequest(
            envelope.GetProperty("eventId").GetGuid(),
            envelope.GetProperty("entityId").GetGuid(),
            data.GetProperty("kind").GetString() == WebOrderPushMessage.Reminder ? PushKind.WebOrderReminder : PushKind.WebOrderAwaitingAcceptance,
            data.GetProperty("fulfillmentType").GetString() ?? string.Empty,
            data.GetProperty("marmitaCount").GetInt32());

        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().InitializeForSystem(tenantId);
        await scope.ServiceProvider.GetRequiredService<WebOrderPushService>().SendAsync(request, cancellationToken);
    }
}
