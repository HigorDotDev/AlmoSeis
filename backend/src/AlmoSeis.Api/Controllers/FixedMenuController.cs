using AlmoSeis.Api.Infrastructure;
using AlmoSeis.Modules.Catalog.Contracts;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Security;
using Microsoft.AspNetCore.Mvc;

namespace AlmoSeis.Api.Controllers;

/// Cardápio fixo (06 §6.5 "Cardápio fixo", ADR-0056/0057). Escritas e leituras por id exigem o módulo `FIXED_MENU`
/// (`403 ModuleNotEnabled`); o `GET /fixed-menu` sem o módulo devolve as listas vazias (ADR-0054 item 14).
/// Cadastro: `catalog.manage`. Pausa e reativação: `catalog.availability` (Q-306).
[ApiController]
[Route("api/v1/fixed-menu")]
public sealed class FixedMenuController : ControllerBase
{
    [HttpGet]
    [HasPermission(Permissions.Orders.View)]
    public async Task<ActionResult<FixedMenuDto>> Get(
        [FromQuery] bool includeInactive,
        [FromServices] IQueryHandler<GetFixedMenuQuery, FixedMenuDto> handler,
        CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new GetFixedMenuQuery(includeInactive), cancellationToken));

    // ---------- Categorias ----------

    [HttpGet("dish-categories/{id:guid}")]
    [HasPermission(Permissions.Orders.View)]
    [RequiresModule(TenantModule.FixedMenu)]
    public async Task<ActionResult<DishCategoryDto>> GetCategory(
        Guid id, [FromServices] IQueryHandler<GetDishCategoryQuery, DishCategoryDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new GetDishCategoryQuery(id), cancellationToken));

    [HttpPost("dish-categories")]
    [HasPermission(Permissions.CatalogManage)]
    [RequiresModule(TenantModule.FixedMenu)]
    public async Task<ActionResult<DishCategoryDto>> CreateCategory(
        [FromBody] CreateDishCategoryCommand command,
        [FromServices] ICommandHandler<CreateDishCategoryCommand, DishCategoryDto> handler,
        CancellationToken cancellationToken)
    {
        var category = await handler.HandleAsync(command, cancellationToken);
        return Created($"/api/v1/fixed-menu/dish-categories/{category.Id}", category);
    }

    [HttpPatch("dish-categories/{id:guid}")]
    [HasPermission(Permissions.CatalogManage)]
    [RequiresModule(TenantModule.FixedMenu)]
    [RequiresIfMatch]
    public async Task<ActionResult<DishCategoryDto>> UpdateCategory(
        Guid id,
        [FromBody] UpdateDishCategoryRequest request,
        [FromServices] ICommandHandler<UpdateDishCategoryCommand, DishCategoryDto> handler,
        CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new UpdateDishCategoryCommand(id, request), cancellationToken));

    // ---------- Pratos ----------

    [HttpGet("dishes/{id:guid}")]
    [HasPermission(Permissions.Orders.View)]
    [RequiresModule(TenantModule.FixedMenu)]
    public async Task<ActionResult<DishDto>> GetDish(
        Guid id, [FromServices] IQueryHandler<GetDishQuery, DishDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new GetDishQuery(id), cancellationToken));

    [HttpPost("dishes")]
    [HasPermission(Permissions.CatalogManage)]
    [RequiresModule(TenantModule.FixedMenu)]
    public async Task<ActionResult<DishDto>> CreateDish(
        [FromBody] CreateDishCommand command,
        [FromServices] ICommandHandler<CreateDishCommand, DishDto> handler,
        CancellationToken cancellationToken)
    {
        var dish = await handler.HandleAsync(command, cancellationToken);
        return Created($"/api/v1/fixed-menu/dishes/{dish.Id}", dish);
    }

    [HttpPatch("dishes/{id:guid}")]
    [HasPermission(Permissions.CatalogManage)]
    [RequiresModule(TenantModule.FixedMenu)]
    [RequiresIfMatch]
    public async Task<ActionResult<DishDto>> UpdateDish(
        Guid id,
        [FromBody] UpdateDishRequest request,
        [FromServices] ICommandHandler<UpdateDishCommand, DishDto> handler,
        CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new UpdateDishCommand(id, request), cancellationToken));

    [HttpPatch("dishes/{id:guid}/availability")]
    [HasPermission(Permissions.CatalogAvailability)]
    [RequiresModule(TenantModule.FixedMenu)]
    [RequiresIfMatch]
    public async Task<ActionResult<DishDto>> SetDishAvailability(
        Guid id,
        [FromBody] SetDishAvailabilityRequest request,
        [FromServices] ICommandHandler<SetDishAvailabilityCommand, DishDto> handler,
        CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new SetDishAvailabilityCommand(id, request), cancellationToken));

    [HttpPost("dishes/{id:guid}/image-upload")]
    [HasPermission(Permissions.CatalogManage)]
    [RequiresModule(TenantModule.FixedMenu)]
    public async Task<ActionResult<ImageUploadDto>> RequestDishImageUpload(
        Guid id,
        [FromBody] RequestImageUploadRequest request,
        [FromServices] ICommandHandler<RequestFixedMenuImageUploadCommand, ImageUploadDto> handler,
        CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await handler.HandleAsync(new RequestFixedMenuImageUploadCommand("DISH", id, request), cancellationToken));

    // ---------- Bebidas ----------

    [HttpGet("beverage-types/{id:guid}")]
    [HasPermission(Permissions.Orders.View)]
    [RequiresModule(TenantModule.FixedMenu)]
    public async Task<ActionResult<BeverageTypeDto>> GetBeverageType(
        Guid id, [FromServices] IQueryHandler<GetBeverageTypeQuery, BeverageTypeDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new GetBeverageTypeQuery(id), cancellationToken));

    [HttpPost("beverage-types")]
    [HasPermission(Permissions.CatalogManage)]
    [RequiresModule(TenantModule.FixedMenu)]
    public async Task<ActionResult<BeverageTypeDto>> CreateBeverageType(
        [FromBody] CreateBeverageTypeCommand command,
        [FromServices] ICommandHandler<CreateBeverageTypeCommand, BeverageTypeDto> handler,
        CancellationToken cancellationToken)
    {
        var type = await handler.HandleAsync(command, cancellationToken);
        return Created($"/api/v1/fixed-menu/beverage-types/{type.Id}", type);
    }

    [HttpPatch("beverage-types/{id:guid}")]
    [HasPermission(Permissions.CatalogManage)]
    [RequiresModule(TenantModule.FixedMenu)]
    [RequiresIfMatch]
    public async Task<ActionResult<BeverageTypeDto>> UpdateBeverageType(
        Guid id,
        [FromBody] UpdateBeverageTypeRequest request,
        [FromServices] ICommandHandler<UpdateBeverageTypeCommand, BeverageTypeDto> handler,
        CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new UpdateBeverageTypeCommand(id, request), cancellationToken));

    [HttpPatch("beverage-types/{id:guid}/availability")]
    [HasPermission(Permissions.CatalogAvailability)]
    [RequiresModule(TenantModule.FixedMenu)]
    [RequiresIfMatch]
    public async Task<ActionResult<BeverageTypeDto>> SetBeverageAvailability(
        Guid id,
        [FromBody] SetBeverageAvailabilityRequest request,
        [FromServices] ICommandHandler<SetBeverageAvailabilityCommand, BeverageTypeDto> handler,
        CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new SetBeverageAvailabilityCommand(id, request), cancellationToken));

    // ---------- Combos ----------

    [HttpGet("combos/{id:guid}")]
    [HasPermission(Permissions.Orders.View)]
    [RequiresModule(TenantModule.FixedMenu)]
    public async Task<ActionResult<ComboDto>> GetCombo(
        Guid id, [FromServices] IQueryHandler<GetComboQuery, ComboDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new GetComboQuery(id), cancellationToken));

    [HttpPost("combos")]
    [HasPermission(Permissions.CatalogManage)]
    [RequiresModule(TenantModule.FixedMenu)]
    public async Task<ActionResult<ComboDto>> CreateCombo(
        [FromBody] CreateComboCommand command,
        [FromServices] ICommandHandler<CreateComboCommand, ComboDto> handler,
        CancellationToken cancellationToken)
    {
        var combo = await handler.HandleAsync(command, cancellationToken);
        return Created($"/api/v1/fixed-menu/combos/{combo.Id}", combo);
    }

    [HttpPatch("combos/{id:guid}")]
    [HasPermission(Permissions.CatalogManage)]
    [RequiresModule(TenantModule.FixedMenu)]
    [RequiresIfMatch]
    public async Task<ActionResult<ComboDto>> UpdateCombo(
        Guid id,
        [FromBody] UpdateComboRequest request,
        [FromServices] ICommandHandler<UpdateComboCommand, ComboDto> handler,
        CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new UpdateComboCommand(id, request), cancellationToken));

    [HttpPatch("combos/{id:guid}/availability")]
    [HasPermission(Permissions.CatalogAvailability)]
    [RequiresModule(TenantModule.FixedMenu)]
    [RequiresIfMatch]
    public async Task<ActionResult<ComboDto>> SetComboAvailability(
        Guid id,
        [FromBody] SetComboAvailabilityRequest request,
        [FromServices] ICommandHandler<SetComboAvailabilityCommand, ComboDto> handler,
        CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new SetComboAvailabilityCommand(id, request), cancellationToken));

    [HttpPost("combos/{id:guid}/image-upload")]
    [HasPermission(Permissions.CatalogManage)]
    [RequiresModule(TenantModule.FixedMenu)]
    public async Task<ActionResult<ImageUploadDto>> RequestComboImageUpload(
        Guid id,
        [FromBody] RequestImageUploadRequest request,
        [FromServices] ICommandHandler<RequestFixedMenuImageUploadCommand, ImageUploadDto> handler,
        CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await handler.HandleAsync(new RequestFixedMenuImageUploadCommand("COMBO", id, request), cancellationToken));

    // ---------- Grupos de adicionais ----------

    [HttpGet("addon-groups/{id:guid}")]
    [HasPermission(Permissions.Orders.View)]
    [RequiresModule(TenantModule.FixedMenu)]
    public async Task<ActionResult<AddonGroupDto>> GetAddonGroup(
        Guid id, [FromServices] IQueryHandler<GetAddonGroupQuery, AddonGroupDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new GetAddonGroupQuery(id), cancellationToken));

    [HttpPost("addon-groups")]
    [HasPermission(Permissions.CatalogManage)]
    [RequiresModule(TenantModule.FixedMenu)]
    public async Task<ActionResult<AddonGroupDto>> CreateAddonGroup(
        [FromBody] CreateAddonGroupCommand command,
        [FromServices] ICommandHandler<CreateAddonGroupCommand, AddonGroupDto> handler,
        CancellationToken cancellationToken)
    {
        var group = await handler.HandleAsync(command, cancellationToken);
        return Created($"/api/v1/fixed-menu/addon-groups/{group.Id}", group);
    }

    [HttpPatch("addon-groups/{id:guid}")]
    [HasPermission(Permissions.CatalogManage)]
    [RequiresModule(TenantModule.FixedMenu)]
    [RequiresIfMatch]
    public async Task<ActionResult<AddonGroupDto>> UpdateAddonGroup(
        Guid id,
        [FromBody] UpdateAddonGroupRequest request,
        [FromServices] ICommandHandler<UpdateAddonGroupCommand, AddonGroupDto> handler,
        CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new UpdateAddonGroupCommand(id, request), cancellationToken));

    [HttpPatch("addon-groups/{id:guid}/availability")]
    [HasPermission(Permissions.CatalogAvailability)]
    [RequiresModule(TenantModule.FixedMenu)]
    [RequiresIfMatch]
    public async Task<ActionResult<AddonGroupDto>> SetAddonAvailability(
        Guid id,
        [FromBody] SetAddonAvailabilityRequest request,
        [FromServices] ICommandHandler<SetAddonAvailabilityCommand, AddonGroupDto> handler,
        CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new SetAddonAvailabilityCommand(id, request), cancellationToken));
}
