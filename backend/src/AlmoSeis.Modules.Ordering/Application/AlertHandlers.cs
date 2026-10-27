using System.Text.Json;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.Modules.Ordering.Domain;
using AlmoSeis.Modules.Ordering.Infrastructure;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Errors;
using AlmoSeis.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace AlmoSeis.Modules.Ordering.Application;

/// Regra de alerta pendente de 04 §4.7 para quem chama (RN-008.6/8.7): evento de outra pessoa, que ele pode
/// ver (produção para `affectsProduction`, pedidos para os demais), sem ciência, em expediente aberto.
internal sealed class AlertCounter(AppDbContext db, ITenantContext tenant, IPermissionChecker permissions)
{
    public async Task<int> CountForOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        (await CountForOrdersAsync([orderId], cancellationToken)).GetValueOrDefault(orderId);

    public async Task<IReadOnlyDictionary<Guid, int>> CountForOrdersAsync(IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken)
    {
        if (orderIds.Count == 0 || tenant.EmployeeId is null)
        {
            return new Dictionary<Guid, int>();
        }

        var rows = await Pending(orderIds.ToArray())
            .GroupBy(e => e.OrderId)
            .Select(g => new { OrderId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.OrderId, r => r.Count);
    }

    public IQueryable<OrderChangeEvent> Pending(Guid[] orderIds)
    {
        var employeeId = tenant.EmployeeId!.Value;
        var seesProduction = permissions.Has(Permissions.Production.View);
        var seesOrders = permissions.Has(Permissions.Orders.View);

        return
            from e in db.Set<OrderChangeEvent>()
            join o in db.Set<Order>() on e.OrderId equals o.Id
            where orderIds.Contains(e.OrderId)
                && e.ActorEmployeeId != employeeId
                && ((e.AffectsProduction && seesProduction) || (!e.AffectsProduction && seesOrders))
                && !db.Set<AlertAcknowledgement>().Any(a => a.OrderChangeEventId == e.Id && a.TenantEmployeeId == employeeId)
                && db.Set<OpenBusinessDayRow>().Any(d => d.Id == o.BusinessDayId)
            select e;
    }
}

internal sealed class GetOrderAlertsQueryHandler(AppDbContext db, AlertCounter alerts, IEmployeeDirectory employees)
    : IQueryHandler<GetOrderAlertsQuery, ItemsDto<OrderAlertDto>>
{
    public async Task<ItemsDto<OrderAlertDto>> HandleAsync(GetOrderAlertsQuery query, CancellationToken cancellationToken)
    {
        if (!await db.Set<Order>().AnyAsync(o => o.Id == query.OrderId, cancellationToken))
        {
            throw new NotFoundException("ORDER", query.OrderId);
        }

        var events = await alerts.Pending([query.OrderId]).AsNoTracking().OrderBy(e => e.OccurredAt).ThenBy(e => e.Id).ToListAsync(cancellationToken);
        var names = await employees.GetDisplayNamesAsync(events.Select(e => e.ActorEmployeeId), cancellationToken);
        return new ItemsDto<OrderAlertDto>(events
            .Select(e =>
            {
                var details = JsonDocument.Parse(e.Details).RootElement.Clone();
                return new OrderAlertDto(
                    e.Id, e.ChangeKind, e.AffectsProduction, e.OrderVersion, e.OccurredAt,
                    new AuthorDto(e.ActorEmployeeId, names.GetValueOrDefault(e.ActorEmployeeId, string.Empty)),
                    AlertSummary.For(e.ChangeKind, details), details);
            })
            .ToList());
    }
}

/// POST /orders/{id}/alerts/acknowledge: ciência só de quem chama; repetir não dá erro (RN-008.7).
internal sealed class AcknowledgeAlertsHandler(AppDbContext db, ITenantContext tenant, TimeProvider time)
    : ICommandHandler<AcknowledgeAlertsCommand, bool>
{
    public async Task<bool> HandleAsync(AcknowledgeAlertsCommand command, CancellationToken cancellationToken)
    {
        var ids = command.Request.EventIds.Distinct().ToArray();
        var known = await db.Set<OrderChangeEvent>()
            .Where(e => e.OrderId == command.OrderId && ids.Contains(e.Id))
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);
        if (known.Count != ids.Length)
        {
            throw BusinessException.Validation("eventIds", "Há alertas que não pertencem a este pedido.");
        }

        var tenantId = tenant.TenantId!.Value;
        var employeeId = tenant.EmployeeId!.Value;
        var now = time.GetUtcNow();
        foreach (var id in ids)
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO alert_acknowledgements (tenant_id, order_change_event_id, tenant_employee_id, acknowledged_at)
                VALUES ({tenantId}, {id}, {employeeId}, {now})
                ON CONFLICT DO NOTHING
                """,
                cancellationToken);
        }

        return true;
    }
}

/// Texto curto do alerta ("+1 Coca-Cola 350ml"), montado dos detalhes gravados.
internal static class AlertSummary
{
    public static string For(OrderChangeKind kind, JsonElement d)
    {
        string? Text(string name) => d.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;

        return kind switch
        {
            OrderChangeKind.ItemAdded => $"+{Text("quantityAfter")} {Text("name")}",
            OrderChangeKind.ItemRemoved => $"−{Text("quantityBefore")} {Text("name")}",
            OrderChangeKind.QuantityChanged => $"{Text("name")}: {Text("quantityBefore")} → {Text("quantityAfter")}",
            OrderChangeKind.ItemNotesChanged => $"Obs. de {Text("name")}: {Text("notesAfter") ?? "removida"}",
            OrderChangeKind.OrderNotesChanged => $"Obs. do pedido: {Text("notesAfter") ?? "removida"}",
            OrderChangeKind.WeighingAdded => $"+ pesagem de {Text("weightGrams")} g",
            OrderChangeKind.WeighingRemoved => $"Pesagem de {Text("weightGrams")} g removida",
            OrderChangeKind.FulfillmentChanged => "Entrega ou retirada alterada",
            OrderChangeKind.OrderCanceled => $"Pedido cancelado: {Text("reason")}",
            _ => kind.ToString(),
        };
    }
}
