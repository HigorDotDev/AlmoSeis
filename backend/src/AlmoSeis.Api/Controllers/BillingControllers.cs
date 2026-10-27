using AlmoSeis.Api.Infrastructure;
using AlmoSeis.Modules.Billing.Contracts;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Errors;
using AlmoSeis.SharedKernel.Security;
using Microsoft.AspNetCore.Mvc;

namespace AlmoSeis.Api.Controllers;

/// GET /subscription (06 §6.4): situação, sem preço nem link (ADR-0017).
[ApiController]
[Route("api/v1/subscription")]
public sealed class SubscriptionController : ControllerBase
{
    [HttpGet]
    [HasPermission(Permissions.SubscriptionView)]
    public async Task<ActionResult<SubscriptionDto>> Get(
        [FromServices] IQueryHandler<GetSubscriptionQuery, SubscriptionDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new GetSubscriptionQuery(), cancellationToken));
}

/// POST /webhooks/asaas (06 §6.15): público, autenticado pelo header `asaas-access-token`. Responde antes de processar.
[ApiController]
[Route("webhooks/asaas")]
public sealed class AsaasWebhookController : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Receive([FromServices] IAsaasWebhookReceiver receiver, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body);
        var payload = await reader.ReadToEndAsync(cancellationToken);
        var receipt = await receiver.ReceiveAsync(Request.Headers["asaas-access-token"].ToString(), payload, cancellationToken);
        return receipt.Accepted ? Ok() : throw new BusinessException(ErrorCodes.Unauthenticated, "Token do webhook inválido.");
    }
}
