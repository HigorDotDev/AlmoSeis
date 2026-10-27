using AlmoSeis.Api.Infrastructure;
using AlmoSeis.Modules.Catalog.Contracts;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Security;
using Microsoft.AspNetCore.Mvc;

namespace AlmoSeis.Api.Controllers;

/// Produtos (06 §6.5).
[ApiController]
[Route("api/v1/products")]
public sealed class ProductsController : ControllerBase
{
    [HttpGet]
    [HasPermission(Permissions.Orders.View)]
    public async Task<ActionResult<ItemsDto<ProductDto>>> List(
        [FromQuery] ProductKind? kind,
        [FromQuery] bool includeInactive,
        [FromServices] IQueryHandler<ListProductsQuery, ItemsDto<ProductDto>> handler,
        CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new ListProductsQuery(kind, includeInactive), cancellationToken));

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.Orders.View)]
    public async Task<ActionResult<ProductDto>> Get(
        Guid id,
        [FromServices] IQueryHandler<GetProductQuery, ProductDto> handler,
        CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new GetProductQuery(id), cancellationToken));

    [HttpPost]
    [HasPermission(Permissions.CatalogManage)]
    public async Task<ActionResult<ProductDto>> Create(
        [FromBody] CreateProductCommand command,
        [FromServices] ICommandHandler<CreateProductCommand, ProductDto> handler,
        CancellationToken cancellationToken)
    {
        var product = await handler.HandleAsync(command, cancellationToken);
        return Created($"/api/v1/products/{product.Id}", product);
    }

    [HttpPatch("{id:guid}")]
    [HasPermission(Permissions.CatalogManage)]
    [RequiresIfMatch]
    public async Task<ActionResult<ProductDto>> Update(
        Guid id,
        [FromBody] UpdateProductRequest request,
        [FromServices] ICommandHandler<UpdateProductCommand, ProductDto> handler,
        CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new UpdateProductCommand(id, request), cancellationToken));
}

/// Tamanhos de marmita (06 §6.5).
[ApiController]
[Route("api/v1/marmita-sizes")]
public sealed class MarmitaSizesController : ControllerBase
{
    [HttpGet]
    [HasPermission(Permissions.Orders.View)]
    public async Task<ActionResult<ItemsDto<MarmitaSizeDto>>> List(
        [FromQuery] bool includeInactive,
        [FromServices] IQueryHandler<ListMarmitaSizesQuery, ItemsDto<MarmitaSizeDto>> handler,
        CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new ListMarmitaSizesQuery(includeInactive), cancellationToken));

    [HttpPost]
    [HasPermission(Permissions.CatalogManage)]
    public async Task<ActionResult<MarmitaSizeDto>> Create(
        [FromBody] CreateMarmitaSizeCommand command,
        [FromServices] ICommandHandler<CreateMarmitaSizeCommand, MarmitaSizeDto> handler,
        CancellationToken cancellationToken)
    {
        var size = await handler.HandleAsync(command, cancellationToken);
        return Created($"/api/v1/marmita-sizes/{size.Id}", size);
    }

    [HttpPatch("{id:guid}")]
    [HasPermission(Permissions.CatalogManage)]
    [RequiresIfMatch]
    public async Task<ActionResult<MarmitaSizeDto>> Update(
        Guid id,
        [FromBody] UpdateMarmitaSizeRequest request,
        [FromServices] ICommandHandler<UpdateMarmitaSizeCommand, MarmitaSizeDto> handler,
        CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new UpdateMarmitaSizeCommand(id, request), cancellationToken));
}
