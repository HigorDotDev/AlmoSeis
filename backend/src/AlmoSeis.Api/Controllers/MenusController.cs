using AlmoSeis.Api.Infrastructure;
using AlmoSeis.Modules.Menu.Contracts;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Security;
using Microsoft.AspNetCore.Mvc;

namespace AlmoSeis.Api.Controllers;

/// Cardápios (06 §6.7).
[ApiController]
[Route("api/v1/menus")]
public sealed class MenusController : ControllerBase
{
    [HttpGet]
    [HasPermission(Permissions.Orders.View)]
    public async Task<ActionResult<ItemsDto<MenuSummaryDto>>> List(
        [FromQuery] MenuType? menuType,
        [FromQuery] MenuKind? kind,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromServices] IQueryHandler<ListMenusQuery, ItemsDto<MenuSummaryDto>> handler,
        CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new ListMenusQuery(menuType, kind, from, to), cancellationToken));

    [HttpGet("current")]
    [HasPermission(Permissions.Orders.View)]
    public async Task<ActionResult<MenuDto>> GetCurrent(
        [FromQuery] MenuType menuType,
        [FromServices] IQueryHandler<GetCurrentMenuQuery, MenuDto> handler,
        CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new GetCurrentMenuQuery(menuType), cancellationToken));

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.Orders.View)]
    public async Task<ActionResult<MenuDto>> Get(
        Guid id,
        [FromServices] IQueryHandler<GetMenuQuery, MenuDto> handler,
        CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new GetMenuQuery(id), cancellationToken));

    [HttpPost]
    [HasPermission(Permissions.MenuManage)]
    public async Task<ActionResult<MenuDto>> Create(
        [FromBody] CreateMenuCommand command,
        [FromServices] ICommandHandler<CreateMenuCommand, MenuDto> handler,
        CancellationToken cancellationToken)
    {
        var menu = await handler.HandleAsync(command, cancellationToken);
        return Created($"/api/v1/menus/{menu.Id}", menu);
    }

    [HttpPut("{id:guid}/items")]
    [HasPermission(Permissions.MenuManage)]
    [RequiresIfMatch]
    public async Task<ActionResult<MenuDto>> ReplaceItems(
        Guid id,
        [FromBody] ReplaceMenuItemsRequest request,
        [FromServices] ICommandHandler<ReplaceMenuItemsCommand, MenuDto> handler,
        CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new ReplaceMenuItemsCommand(id, request), cancellationToken));

    [HttpPatch("{id:guid}/items/{itemId:guid}")]
    [HasPermission(Permissions.MenuManage)]
    [RequiresIfMatch]
    public async Task<ActionResult<MenuDto>> UpdateItem(
        Guid id,
        Guid itemId,
        [FromBody] UpdateMenuItemRequest request,
        [FromServices] ICommandHandler<UpdateMenuItemCommand, MenuDto> handler,
        CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new UpdateMenuItemCommand(id, itemId, request), cancellationToken));

    [HttpPost("{id:guid}/clone")]
    [HasPermission(Permissions.MenuManage)]
    public async Task<ActionResult<MenuDto>> Clone(
        Guid id,
        [FromBody] CloneMenuRequest request,
        [FromServices] ICommandHandler<CloneMenuCommand, MenuDto> handler,
        CancellationToken cancellationToken)
    {
        var menu = await handler.HandleAsync(new CloneMenuCommand(id, request), cancellationToken);
        return Created($"/api/v1/menus/{menu.Id}", menu);
    }
}
