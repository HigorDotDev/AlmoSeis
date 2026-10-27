using AlmoSeis.Api.Infrastructure;
using AlmoSeis.Modules.Payments.Contracts;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Security;
using Microsoft.AspNetCore.Mvc;

namespace AlmoSeis.Api.Controllers;

/// Pagamentos e estornos (06 §6.10). O If-Match das escritas é a versão do **pedido**.
[ApiController]
public sealed class PaymentsController : ControllerBase
{
    [HttpPost("api/v1/orders/{orderId:guid}/payments")]
    [HasPermission(Permissions.Payments.Register)]
    [Idempotent]
    [RequiresIfMatch]
    public async Task<ActionResult<RegisterPaymentResultDto>> Register(
        Guid orderId, [FromBody] RegisterPaymentRequest request,
        [FromServices] ICommandHandler<RegisterPaymentCommand, RegisterPaymentResultDto> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new RegisterPaymentCommand(orderId, request), cancellationToken);
        return Created($"/api/v1/payments/{result.Payment.Id}", result);
    }

    [HttpGet("api/v1/orders/{orderId:guid}/payments")]
    [HasPermission(Permissions.Orders.View)]
    public async Task<ActionResult<ItemsDto<PaymentDto>>> ListForOrder(
        Guid orderId, [FromServices] IQueryHandler<ListOrderPaymentsQuery, ItemsDto<PaymentDto>> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new ListOrderPaymentsQuery(orderId), cancellationToken));

    [HttpGet("api/v1/payments/{id:guid}")]
    [HasPermission(Permissions.Orders.View)]
    public async Task<ActionResult<PaymentDto>> Get(
        Guid id, [FromServices] IQueryHandler<GetPaymentQuery, PaymentDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new GetPaymentQuery(id), cancellationToken));

    [HttpPost("api/v1/payments/{id:guid}/reverse")]
    [HasPermission(Permissions.Payments.Reverse)]
    [Idempotent]
    [RequiresIfMatch]
    public async Task<ActionResult<ReversePaymentResultDto>> Reverse(
        Guid id, [FromBody] ReversePaymentRequest request,
        [FromServices] ICommandHandler<ReversePaymentCommand, ReversePaymentResultDto> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ReversePaymentCommand(id, request), cancellationToken);
        return Created($"/api/v1/payments/{result.Reversal.PaymentId}", result);
    }
}
