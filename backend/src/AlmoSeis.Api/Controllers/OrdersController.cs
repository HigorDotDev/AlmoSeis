using AlmoSeis.Api.Infrastructure;
using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Security;
using Microsoft.AspNetCore.Mvc;

namespace AlmoSeis.Api.Controllers;

/// Pedidos, itens, pesagens, status, cancelamento, histórico e alertas (06 §6.9).
[ApiController]
[Route("api/v1/orders")]
public sealed class OrdersController : ControllerBase
{
    [HttpGet]
    [HasPermission(Permissions.Orders.View)]
    public async Task<ActionResult<PageDto<OrderSummaryDto>>> List(
        [FromQuery] Guid? businessDayId,
        [FromQuery] bool? allDays,
        [FromQuery] OrderType? orderType,
        [FromQuery] bool? isPaid,
        [FromQuery] CancellationStatus? cancellationStatus,
        [FromQuery] ProductionStatus? productionStatus,
        [FromQuery] Guid? customerId,
        [FromQuery] string? search,
        [FromQuery] string? cursor,
        [FromServices] IQueryHandler<ListOrdersQuery, PageDto<OrderSummaryDto>> handler,
        CancellationToken cancellationToken,
        [FromQuery] int limit = 50) =>
        Ok(await handler.HandleAsync(
            new ListOrdersQuery(businessDayId, allDays ?? false, orderType, isPaid, cancellationStatus, productionStatus, customerId, search, limit, cursor),
            cancellationToken));

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.Orders.View)]
    public async Task<ActionResult<OrderDto>> Get(
        Guid id, [FromServices] IQueryHandler<GetOrderQuery, OrderDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new GetOrderQuery(id), cancellationToken));

    [HttpPost]
    [HasPermission(Permissions.Orders.Create)]
    [Idempotent]
    public async Task<ActionResult<OrderDto>> Create(
        [FromBody] CreateOrderCommand command,
        [FromServices] ICommandHandler<CreateOrderCommand, OrderDto> handler,
        CancellationToken cancellationToken)
    {
        var order = await handler.HandleAsync(command, cancellationToken);
        return Created($"/api/v1/orders/{order.Id}", order);
    }

    [HttpPatch("{id:guid}")]
    [HasPermission(Permissions.Orders.Edit)]
    [RequiresIfMatch]
    public async Task<ActionResult<OrderDto>> Update(
        Guid id, [FromBody] UpdateOrderRequest request,
        [FromServices] ICommandHandler<UpdateOrderCommand, OrderDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new UpdateOrderCommand(id, request), cancellationToken));

    [HttpPost("{id:guid}/items")]
    [HasPermission(Permissions.Orders.Edit)]
    [Idempotent]
    [RequiresIfMatch]
    public async Task<ActionResult<OrderDto>> AddItems(
        Guid id, [FromBody] AddOrderItemsRequest request,
        [FromServices] ICommandHandler<AddOrderItemsCommand, OrderDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new AddOrderItemsCommand(id, request), cancellationToken));

    [HttpPatch("{id:guid}/items/{itemId:guid}")]
    [HasPermission(Permissions.Orders.Edit)]
    [RequiresIfMatch]
    public async Task<ActionResult<OrderDto>> UpdateItem(
        Guid id, Guid itemId, [FromBody] UpdateOrderItemRequest request,
        [FromServices] ICommandHandler<UpdateOrderItemCommand, OrderDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new UpdateOrderItemCommand(id, itemId, request), cancellationToken));

    [HttpDelete("{id:guid}/items/{itemId:guid}")]
    [HasPermission(Permissions.Orders.Edit)]
    [RequiresIfMatch]
    public async Task<ActionResult<OrderDto>> RemoveItem(
        Guid id, Guid itemId,
        [FromServices] ICommandHandler<RemoveOrderItemCommand, OrderDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new RemoveOrderItemCommand(id, itemId), cancellationToken));

    [HttpPost("{id:guid}/weighings")]
    [HasPermission(Permissions.Orders.Edit)]
    [Idempotent]
    [RequiresIfMatch]
    public async Task<ActionResult<OrderDto>> AddWeighings(
        Guid id, [FromBody] AddWeighingsRequest request,
        [FromServices] ICommandHandler<AddWeighingsCommand, OrderDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new AddWeighingsCommand(id, request), cancellationToken));

    [HttpDelete("{id:guid}/weighings/{weighingId:guid}")]
    [HasPermission(Permissions.Orders.Edit)]
    [RequiresIfMatch]
    public async Task<ActionResult<OrderDto>> RemoveWeighing(
        Guid id, Guid weighingId,
        [FromServices] ICommandHandler<RemoveWeighingCommand, OrderDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new RemoveWeighingCommand(id, weighingId), cancellationToken));

    [HttpPatch("{id:guid}/status")]
    [HasPermission(Permissions.Production.Update)]
    [RequiresIfMatch]
    public async Task<ActionResult<OrderDto>> ChangeStatus(
        Guid id, [FromBody] ChangeProductionStatusRequest request,
        [FromServices] ICommandHandler<ChangeProductionStatusCommand, OrderDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new ChangeProductionStatusCommand(id, request), cancellationToken));

    [HttpPost("{id:guid}/cancel")]
    [HasPermission(Permissions.Orders.Cancel)]
    [RequiresIfMatch]
    public async Task<ActionResult<OrderDto>> Cancel(
        Guid id, [FromBody] CancelOrderRequest request,
        [FromServices] ICommandHandler<CancelOrderCommand, OrderDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new CancelOrderCommand(id, request), cancellationToken));

    [HttpGet("{id:guid}/history")]
    [HasPermission(Permissions.Orders.View)]
    public async Task<ActionResult<PageDto<OrderHistoryEntryDto>>> History(
        Guid id, [FromQuery] string? cursor,
        [FromServices] IQueryHandler<GetOrderHistoryQuery, PageDto<OrderHistoryEntryDto>> handler,
        CancellationToken cancellationToken, [FromQuery] int limit = 50) =>
        Ok(await handler.HandleAsync(new GetOrderHistoryQuery(id, limit, cursor), cancellationToken));

    [HttpGet("{id:guid}/alerts")]
    [HasPermission(Permissions.Orders.View)]
    public async Task<ActionResult<ItemsDto<OrderAlertDto>>> Alerts(
        Guid id, [FromServices] IQueryHandler<GetOrderAlertsQuery, ItemsDto<OrderAlertDto>> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new GetOrderAlertsQuery(id), cancellationToken));

    [HttpPost("{id:guid}/alerts/acknowledge")]
    [HasPermission(Permissions.Orders.View)]
    public async Task<IActionResult> AcknowledgeAlerts(
        Guid id, [FromBody] AcknowledgeAlertsRequest request,
        [FromServices] ICommandHandler<AcknowledgeAlertsCommand, bool> handler, CancellationToken cancellationToken)
    {
        await handler.HandleAsync(new AcknowledgeAlertsCommand(id, request), cancellationToken);
        return NoContent();
    }
}

/// Painel da cozinha (06 §6.9).
[ApiController]
[Route("api/v1/production/orders")]
public sealed class ProductionController : ControllerBase
{
    [HttpGet]
    [HasPermission(Permissions.Production.View)]
    public async Task<ActionResult<ProductionBoardDto>> Board(
        [FromServices] IQueryHandler<GetProductionBoardQuery, ProductionBoardDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new GetProductionBoardQuery(), cancellationToken));
}
