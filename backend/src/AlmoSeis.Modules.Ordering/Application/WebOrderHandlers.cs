using AlmoSeis.Infrastructure.Audit;
using AlmoSeis.Infrastructure.Concurrency;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Closing.Contracts;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.Modules.Ordering.Domain;
using AlmoSeis.Modules.Catalog.Contracts;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;
using AlmoSeis.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Modules.Ordering.Application;

/// Configuração do pedido da página (ADR-0046 itens 14 a 16). Seção `WebOrders`.
public sealed class WebOrderOptions
{
    public const string Section = "WebOrders";

    /// Prazo de aceite a partir do envio (decisão do usuário: 10 min, revisitar após o piloto).
    public int AcceptanceMinutes { get; set; } = 10;

    public bool ExpiryJobEnabled { get; set; } = true;

    public int ExpiryIntervalSeconds { get; set; } = 30;

    /// Retenção da página (ADR-0052): anonimização dos não aceitos, tokens vencidos e hash do IP.
    public bool RetentionJobEnabled { get; set; } = true;

    public int RetentionIntervalMinutes { get; set; } = 60;

    /// Lembrete por push sem resposta (ADR-0046 item 15, ADR-0049): minutos depois do envio. 0 desliga.
    public int ReminderMinutes { get; set; } = 3;

    public TimeSpan AcceptanceWindow => TimeSpan.FromMinutes(AcceptanceMinutes);
}

/// POST /orders/{id}/accept [I][M] (ADR-0046 itens 6 a 9).
internal sealed class AcceptOrderHandler(
    AppDbContext db,
    ITenantContext tenant,
    IConcurrencyContext concurrency,
    IBusinessDayReader businessDays,
    OrderNumberGenerator numbers,
    WebOrderCustomerLinker customers,
    IEmployeeDirectory employees,
    IAuditWriter audit,
    OrderDtoBuilder dtos,
    TimeProvider time) : ICommandHandler<AcceptOrderCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(AcceptOrderCommand command, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var order = await WebOrderRules.LoadForDecisionAsync(db, businessDays, command.OrderId, concurrency.ExpectedVersion, now, cancellationToken);

        // Só depois de tudo validado o número é consumido; se o UPDATE do pedido perder a disputa pela versão, o
        // rollback devolve o número ao contador, e a sequência do dia não tem buracos (ADR-0046 item 8).
        var customerId = await customers.LinkAsync(order, cancellationToken);
        var number = await numbers.NextAsync(order.BusinessDate, cancellationToken);
        var employeeId = tenant.EmployeeId!.Value;
        var name = (await employees.GetDisplayNamesAsync([employeeId], cancellationToken)).GetValueOrDefault(employeeId);
        order.Accept(number, employeeId, name, customerId, command.Request.EstimatedReadyMinutes, now);

        audit.Add("OrderAccepted", order.SyncEntityType, order.Id, order.Id,
            before: new { acceptanceStatus = "AWAITING_ACCEPTANCE" },
            after: new { acceptanceStatus = "ACCEPTED", order.OrderNumber, order.EstimatedReadyMinutes, order.CustomerId, totalAmount = order.TotalAmount.ToString() });
        return await dtos.BuildAsync(order, cancellationToken);
    }
}

/// POST /orders/{id}/reject [M] (ADR-0046 item 12).
internal sealed class RejectOrderHandler(
    AppDbContext db,
    ITenantContext tenant,
    IConcurrencyContext concurrency,
    IBusinessDayReader businessDays,
    IAuditWriter audit,
    OrderDtoBuilder dtos,
    TimeProvider time) : ICommandHandler<RejectOrderCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(RejectOrderCommand command, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var order = await WebOrderRules.LoadForDecisionAsync(db, businessDays, command.OrderId, concurrency.ExpectedVersion, now, cancellationToken);

        order.Reject(command.Request.ReasonCode, command.Request.Message, tenant.EmployeeId!.Value, now);
        audit.Add("OrderRejected", order.SyncEntityType, order.Id, order.Id,
            before: new { acceptanceStatus = "AWAITING_ACCEPTANCE" },
            // Só o código: o texto livre da recusa pode ter dado do cliente e fica só no pedido (ADR-0052 item 7).
            after: new
            {
                acceptanceStatus = "REJECTED",
                reasonCode = UpperSnakeEnumConverter<RejectionReason>.ToText(order.RejectionReasonCode!.Value),
            });
        return await dtos.BuildAsync(order, cancellationToken);
    }
}

/// PATCH /orders/{id}/fulfillment [M] (ADR-0046 item 18). "Concluir e marcar" (`completeProduction`) faz as duas
/// transições na mesma transação e exige também `production.update`.
internal sealed class ChangeFulfillmentHandler(
    AppDbContext db,
    IConcurrencyContext concurrency,
    IPermissionChecker permissions,
    IAuditWriter audit,
    OrderDtoBuilder dtos,
    TimeProvider time) : ICommandHandler<ChangeFulfillmentCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(ChangeFulfillmentCommand command, CancellationToken cancellationToken)
    {
        var order = await OrderRules.LoadAsync(db, command.OrderId, cancellationToken);
        order.EnsureVersion(concurrency.ExpectedVersion);
        var now = time.GetUtcNow();
        var to = command.Request.FulfillmentStatus;

        var leaving = (order.FulfillmentStatus ?? FulfillmentStatus.Pending) == FulfillmentStatus.Pending
            && to is FulfillmentStatus.OutForDelivery or FulfillmentStatus.PickedUp;
        if (command.Request.CompleteProduction && leaving && order.ProductionStatus is { } production && production != ProductionStatus.Completed)
        {
            if (!permissions.Has(Permissions.Production.Update))
            {
                throw new BusinessException(
                    ErrorCodes.PermissionDenied,
                    extensions: new Dictionary<string, object?> { ["permission"] = Permissions.Production.Update });
            }

            order.ChangeProductionStatus(ProductionStatus.Completed, now);
            audit.Add("ProductionStatusChanged", order.SyncEntityType, order.Id, order.Id,
                before: new { productionStatus = UpperSnakeEnumConverter<ProductionStatus>.ToText(production) },
                after: new { productionStatus = "COMPLETED" });
        }

        var from = order.FulfillmentStatus;
        order.ChangeFulfillment(to, now);
        if (order.FulfillmentStatus != from)
        {
            audit.Add("OrderFulfillmentChanged", order.SyncEntityType, order.Id, order.Id,
                before: new { fulfillmentStatus = Text(from) }, after: new { fulfillmentStatus = Text(order.FulfillmentStatus) });
        }

        return await dtos.BuildAsync(order, cancellationToken);
    }

    private static string? Text(FulfillmentStatus? status) => status is { } s ? UpperSnakeEnumConverter<FulfillmentStatus>.ToText(s) : null;
}

/// Um pedido vencido do job de expiração (ADR-0046 item 14), no contexto do tenant e sem ator (`Origin = SYSTEM`).
/// Disputa com o aceite pela versão: quem gravar primeiro vence, o outro recebe conflito (item 16).
internal sealed record ExpireWebOrderCommand(Guid OrderId);

internal sealed class ExpireWebOrderHandler(
    AppDbContext db,
    IBusinessDayReader businessDays,
    IAuditWriter audit,
    TimeProvider time) : ICommandHandler<ExpireWebOrderCommand, bool>
{
    public async Task<bool> HandleAsync(ExpireWebOrderCommand command, CancellationToken cancellationToken)
    {
        // FOR SHARE no expediente, como o aceite: a finalização do fechamento não corre ao mesmo tempo.
        var day = await businessDays.GetOpenAsync(cancellationToken);
        var order = await db.Set<Order>().SingleOrDefaultAsync(o => o.Id == command.OrderId, cancellationToken);
        if (order is null || day?.Id != order.BusinessDayId || !order.Expire(time.GetUtcNow()))
        {
            return false;
        }

        audit.Add("WebOrderExpired", order.SyncEntityType, order.Id, order.Id,
            before: new { acceptanceStatus = "AWAITING_ACCEPTANCE", order.AcceptanceDeadline },
            after: new { acceptanceStatus = "EXPIRED" },
            origin: AuditOrigins.System);
        return true;
    }
}

/// Desistência do cliente (ADR-0046 item 17). A página chama pela rota de acompanhamento
/// (`WithdrawWebOrderByTrackingHandler`); este comando interno é o mesmo caso de uso, usado pelos testes.
internal sealed record WithdrawWebOrderCommand(Guid OrderId);

internal sealed class WithdrawWebOrderHandler(WebOrderWithdrawal withdrawal) : ICommandHandler<WithdrawWebOrderCommand, bool>
{
    public async Task<bool> HandleAsync(WithdrawWebOrderCommand command, CancellationToken cancellationToken)
    {
        await withdrawal.WithdrawAsync(command.OrderId, cancellationToken);
        return true;
    }
}

/// Desistência (ADR-0046 item 17): `WITHDRAWN` com auditoria `WEB`, sem ator. Idempotente pelo estado: o pedido que já é
/// desistência volta como está, sem nova auditoria nem evento (a página repete o `POST` quando a resposta se perde).
/// Prazo vencido → `WebOrderExpired`; aceito ou recusado → `OrderNotAwaitingAcceptance`.
internal sealed class WebOrderWithdrawal(AppDbContext db, IBusinessDayReader businessDays, IAuditWriter audit, TimeProvider time)
{
    public async Task<Order> WithdrawAsync(Guid orderId, CancellationToken cancellationToken)
    {
        // FOR SHARE no expediente, como toda escrita de venda: a finalização do fechamento não corre ao mesmo tempo.
        await businessDays.GetOpenAsync(cancellationToken);
        var order = await OrderRules.LoadAsync(db, orderId, cancellationToken);
        if (order.AcceptanceStatus == AcceptanceStatus.Withdrawn)
        {
            return order;
        }

        var before = UpperSnakeEnumConverter<AcceptanceStatus>.ToText(order.AcceptanceStatus);
        order.Withdraw(time.GetUtcNow());

        audit.Add("WebOrderWithdrawn", order.SyncEntityType, order.Id, order.Id,
            before: new { acceptanceStatus = before }, after: new { acceptanceStatus = "WITHDRAWN" }, origin: AuditOrigins.Web);
        return order;
    }
}

/// Envio do pedido "pagar na entrega/retirada" (ADR-0046, ADR-0044). A rota pública (`PlaceWebOrderHandler`) usa o
/// `WebOrderSubmitter` direto, depois das validações da página; este comando interno é o mesmo caso de uso, usado
/// pelos testes. Tenant do contexto, sem funcionário.
internal sealed record SubmitWebOrderCommand(
    FulfillmentType FulfillmentType,
    string CustomerName,
    string? CustomerPhone,
    DeliveryAddressDto? DeliveryAddress,
    Money DeliveryFee,
    string? Notes,
    IReadOnlyList<OrderItemInput> Items,
    ExpectedPaymentMethod? ExpectedPaymentMethod = null,
    Money? ChangeFor = null,
    WebOrderRiskFlag? RiskFlag = null,
    DeliveryPricing? Pricing = null);

internal sealed class SubmitWebOrderHandler(WebOrderSubmitter submitter) : ICommandHandler<SubmitWebOrderCommand, Guid>
{
    public async Task<Guid> HandleAsync(SubmitWebOrderCommand command, CancellationToken cancellationToken) =>
        (await submitter.SubmitAsync(command, cancellationToken)).Id;
}

internal sealed class WebOrderSubmitter(
    AppDbContext db,
    ITenantContext tenant,
    IIdGenerator ids,
    IBusinessDayReader businessDays,
    OrderItemResolver items,
    IAuditWriter audit,
    IOptions<WebOrderOptions> options,
    TimeProvider time)
{
    public async Task<Order> SubmitAsync(SubmitWebOrderCommand command, CancellationToken cancellationToken)
    {
        var day = await businessDays.GetOpenAsync(cancellationToken) ?? throw new BusinessException(ErrorCodes.BusinessDayRequired);
        // Todo pedido do site usa o preço de site (ADR-0057 item 6).
        var resolved = await items.ResolveAsync(command.Items, day.BusinessDate, PriceList.Site, cancellationToken);
        var now = time.GetUtcNow();

        var order = Order.SubmitFromWeb(
            ids.New(), tenant.TenantId!.Value,
            new NewOrder(
                OrderType.Marmita, command.FulfillmentType, null, command.CustomerName, command.CustomerPhone, command.DeliveryAddress,
                command.DeliveryFee, null, null, command.Notes, resolved, [], command.Pricing),
            day.Id, day.BusinessDate, now + options.Value.AcceptanceWindow, ids.New, now,
            new WebOrderExtras(command.ExpectedPaymentMethod, command.ChangeFor, command.RiskFlag));
        db.Add(order);

        // Sem nome, telefone nem endereço (ADR-0024).
        audit.Add("WebOrderSubmitted", order.SyncEntityType, order.Id, order.Id, after: new
        {
            acceptanceStatus = "AWAITING_ACCEPTANCE",
            fulfillmentType = UpperSnakeEnumConverter<FulfillmentType>.ToText(order.FulfillmentType),
            itemCount = order.Items.Count,
            totalAmount = order.TotalAmount.ToString(),
            order.AcceptanceDeadline,
            riskFlag = order.RiskFlag is { } risk ? UpperSnakeEnumConverter<WebOrderRiskFlag>.ToText(risk) : null,
        }, origin: AuditOrigins.Web);
        return order;
    }
}

/// Porta do Closing (ADR-0046 item 13): na finalização, recusa os pedidos do site ainda aguardando aceite.
internal sealed class AwaitingWebOrdersRejector(AppDbContext db, IAuditWriter audit, TimeProvider time) : IAwaitingWebOrdersRejector
{
    public async Task<int> RejectAwaitingAsync(Guid businessDayId, CancellationToken cancellationToken)
    {
        var orders = await db.Set<Order>()
            .Where(o => o.BusinessDayId == businessDayId && o.AcceptanceStatus == AcceptanceStatus.AwaitingAcceptance)
            .ToListAsync(cancellationToken);
        var now = time.GetUtcNow();
        foreach (var order in orders)
        {
            order.RejectBecauseBusinessDayClosed(now);
            audit.Add("WebOrderAutoRejected", order.SyncEntityType, order.Id, order.Id,
                before: new { acceptanceStatus = "AWAITING_ACCEPTANCE" },
                after: new { acceptanceStatus = "REJECTED", reasonCode = "BUSINESS_DAY_CLOSED" },
                origin: AuditOrigins.System);
        }

        return orders.Count;
    }
}

/// Vínculo com `Customer` no aceite (ADR-0044 item 20): pelo telefone, sem alterar o cadastro existente; sem cadastro,
/// cria um (só nome e telefone: o endereço fica no snapshot do pedido).
internal sealed class WebOrderCustomerLinker(AppDbContext db, ITenantContext tenant, IIdGenerator ids, TimeProvider time)
{
    public async Task<Guid?> LinkAsync(Order order, CancellationToken cancellationToken)
    {
        if (order.CustomerPhone is not { } phone)
        {
            return null;
        }

        // Dois aceites com o mesmo telefone novo (inclusive o mesmo pedido aceito por duas pessoas) esperam um pelo
        // outro aqui: o segundo acha o cadastro do primeiro em vez de estourar ux_customers_phone.
        var tenantId = tenant.TenantId!.Value;
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"customer-phone:" + tenantId + ":" + phone}, 0))", cancellationToken);

        var existing = await db.Set<Customer>().AsNoTracking()
            .Where(c => c.PhoneNumber == phone)
            .Select(c => (Guid?)c.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var customer = Customer.Create(ids.New(), tenantId, order.CustomerName!, phone, null, [], ids.New, time.GetUtcNow());
        db.Add(customer);
        return customer.Id;
    }
}

internal static class WebOrderRules
{
    /// Aceite e recusa: o prazo vencido responde `WebOrderExpired` mesmo com `If-Match` antigo (o job já mudou a
    /// versão); fora isso, o `If-Match` vem primeiro, para o segundo aceite simultâneo receber o `current` ("Já
    /// aceito por Ana", item 9). O expediente do pedido é lido com `FOR SHARE`, como em toda escrita de venda.
    public static async Task<Order> LoadForDecisionAsync(
        AppDbContext db, IBusinessDayReader businessDays, Guid orderId, int expectedVersion, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var day = await businessDays.GetOpenAsync(cancellationToken);
        var order = await OrderRules.LoadAsync(db, orderId, cancellationToken);
        if (order.HasAcceptanceExpired(now))
        {
            throw new BusinessException(ErrorCodes.WebOrderExpired);
        }

        order.EnsureVersion(expectedVersion);
        order.EnsureAwaitingAcceptance(now);
        if (day?.Id != order.BusinessDayId)
        {
            throw new BusinessException(ErrorCodes.BusinessDayRequired);
        }

        return order;
    }
}
