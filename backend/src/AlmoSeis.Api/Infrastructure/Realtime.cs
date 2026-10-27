using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlmoSeis.Infrastructure.Realtime;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AlmoSeis.Api.Infrastructure;

/// `/hubs/realtime` (06 §6.14, ADR-0009). Só recebe: nenhum método de cliente. Os grupos vêm das permissões do token.
[Authorize]
public sealed class RealtimeHub : Hub
{
    /// O hub roda num escopo próprio do SignalR, sem o ITenantContext da requisição: tenant, funcionário e
    /// permissões vêm do acesso validado no handshake (EmployeeAccessValidator).
    public override async Task OnConnectedAsync()
    {
        if (Context.GetHttpContext()?.Items[EmployeeAccessValidator.ItemKey] is not EmployeeAccess access)
        {
            Context.Abort();
            return;
        }

        foreach (var group in RealtimeGroups.ForConnection(access.TenantId, access.EmployeeId, access.Permissions))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, group);
        }

        await base.OnConnectedAsync();
    }
}

/// Tabela de grupos de 06 §6.14.
public static class RealtimeGroups
{
    public const string ClientMethod = "event";

    public static string Tenant(Guid tenantId) => $"tenant:{tenantId}";

    public static string Orders(Guid tenantId) => $"tenant:{tenantId}:orders";

    public static string Production(Guid tenantId) => $"tenant:{tenantId}:production";

    public static string Closing(Guid tenantId) => $"tenant:{tenantId}:closing";

    public static string Catalog(Guid tenantId) => $"tenant:{tenantId}:catalog";

    public static string Employee(Guid employeeId) => $"employee:{employeeId}";

    public static IEnumerable<string> ForConnection(Guid tenantId, Guid employeeId, IReadOnlySet<string> permissions)
    {
        yield return Tenant(tenantId);
        yield return Catalog(tenantId);
        yield return Employee(employeeId);
        if (permissions.Contains(Permissions.Orders.View))
        {
            yield return Orders(tenantId);
        }

        if (permissions.Contains(Permissions.Production.View))
        {
            yield return Production(tenantId);
        }

        if (permissions.Contains(Permissions.Closing.View))
        {
            yield return Closing(tenantId);
        }
    }

    /// Grupos de um evento. `OrderCreated` de quilo não vai para a produção (RN-006.7).
    public static IReadOnlyList<string> ForEvent(Guid tenantId, string eventType, JsonObject envelope) => eventType switch
    {
        "OrderCreated" => envelope["data"]?["orderType"]?.GetValue<string>() == "KILO"
            ? [Orders(tenantId)]
            : [Orders(tenantId), Production(tenantId)],
        "OrderUpdated" or "OrderStatusChanged" or "OrderCanceled" or "OrderAlertCreated" => [Orders(tenantId), Production(tenantId)],
        "OrderPaymentRegistered" => [Orders(tenantId), Closing(tenantId)],
        "OrderPaymentConfirmed" or "OrderPaymentReversed" => [Orders(tenantId), Production(tenantId), Closing(tenantId)],
        "MenuUpdated" or "CatalogUpdated" => [Catalog(tenantId)],
        "BusinessDayOpened" or "ClosingFinalized" or "SubscriptionStatusChanged" => [Tenant(tenantId)],
        "ClosingUpdated" => [Closing(tenantId)],
        // Ciência: o próprio funcionário que agiu. Acesso revogado: o funcionário afetado (a entidade do evento).
        "OrderAlertAcknowledged" when envelope["actorEmployeeId"]?.GetValue<Guid>() is { } actor => [Employee(actor)],
        "AccessRevoked" when envelope["entityId"]?.GetValue<Guid>() is { } employee => [Employee(employee)],
        _ => [],
    };

    /// Eventos que mudam o resumo do fechamento: cada um agenda um `ClosingUpdated` agrupado.
    public static bool AffectsClosing(string eventType) =>
        eventType is "OrderPaymentRegistered" or "OrderPaymentConfirmed" or "OrderPaymentReversed" or "ClosingUpdated";
}

/// Publica o envelope do outbox nos grupos do SignalR. `ClosingUpdated` sai no máximo 1 vez a cada 5 s por tenant
/// (06 §6.14): o primeiro sai na hora, os seguintes dentro da janela viram um só no fim dela.
public sealed class SignalRRealtimePublisher(IHubContext<RealtimeHub> hub, TimeProvider time, ILogger<SignalRRealtimePublisher> logger)
    : IRealtimePublisher
{
    public static readonly TimeSpan ClosingWindow = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<Guid, ClosingThrottle> _closing = new();

    public async Task PublishAsync(Guid tenantId, string eventType, string payload, CancellationToken cancellationToken)
    {
        var envelope = JsonNode.Parse(payload)!.AsObject();
        if (eventType != "ClosingUpdated")
        {
            var groups = RealtimeGroups.ForEvent(tenantId, eventType, envelope);
            if (groups.Count > 0)
            {
                await hub.Clients.Groups(groups).SendAsync(RealtimeGroups.ClientMethod, JsonSerializer.Deserialize<JsonElement>(payload), cancellationToken);
            }
        }

        if (RealtimeGroups.AffectsClosing(eventType))
        {
            ScheduleClosingUpdated(tenantId, envelope);
        }
    }

    private void ScheduleClosingUpdated(Guid tenantId, JsonObject trigger)
    {
        var throttle = _closing.GetOrAdd(tenantId, _ => new ClosingThrottle());
        TimeSpan delay;
        lock (throttle)
        {
            throttle.Latest = trigger;
            if (throttle.Pending)
            {
                return;
            }

            var now = time.GetUtcNow();
            delay = throttle.LastSent + ClosingWindow - now;
            throttle.Pending = true;
        }

        _ = SendClosingUpdatedAsync(tenantId, throttle, delay);
    }

    private async Task SendClosingUpdatedAsync(Guid tenantId, ClosingThrottle throttle, TimeSpan delay)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, time);
            }

            JsonObject envelope;
            lock (throttle)
            {
                envelope = (JsonObject)throttle.Latest!.DeepClone();
                throttle.Pending = false;
                throttle.LastSent = time.GetUtcNow();
            }

            // Sinal "recarregue o resumo": seq e entidade do último evento que o motivou, sem `data`.
            envelope["eventId"] = Guid.CreateVersion7(time.GetUtcNow());
            envelope["eventType"] = "ClosingUpdated";
            envelope["data"] = null;
            await hub.Clients.Group(RealtimeGroups.Closing(tenantId))
                .SendAsync(RealtimeGroups.ClientMethod, JsonSerializer.Deserialize<JsonElement>(envelope.ToJsonString()));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao publicar ClosingUpdated do tenant {TenantId}.", tenantId);
        }
    }

    private sealed class ClosingThrottle
    {
        public DateTimeOffset LastSent { get; set; } = DateTimeOffset.MinValue;

        public bool Pending { get; set; }

        public JsonObject? Latest { get; set; }
    }
}
