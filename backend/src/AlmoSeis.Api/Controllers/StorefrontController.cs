using AlmoSeis.Api.Infrastructure;
using AlmoSeis.Modules.Storefront.Contracts;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Security;
using Microsoft.AspNetCore.Mvc;

namespace AlmoSeis.Api.Controllers;

/// Configuração da página de pedidos pelo restaurante (06 §6.18, ADR-0044, ADR-0046 item 22).
[ApiController]
[Route("api/v1/storefront")]
public sealed class StorefrontController : ControllerBase
{
    [HttpGet]
    [HasPermission(Permissions.Orders.View)]
    public async Task<ActionResult<StorefrontDto>> Get(
        [FromServices] IQueryHandler<GetStorefrontQuery, StorefrontDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new GetStorefrontQuery(), cancellationToken));

    [HttpPut]
    [HasPermission(Permissions.StorefrontManage)]
    [RequiresIfMatch]
    public async Task<ActionResult<StorefrontDto>> Update(
        [FromBody] UpdateStorefrontRequest request,
        [FromServices] ICommandHandler<UpdateStorefrontCommand, StorefrontDto> handler,
        CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new UpdateStorefrontCommand(request), cancellationToken));
}
