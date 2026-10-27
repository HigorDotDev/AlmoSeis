using AlmoSeis.Api.Infrastructure;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Errors;
using AlmoSeis.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlmoSeis.Api.Controllers;

/// Aparelho compartilhado (06 §6.3, ADR-0027).
[ApiController]
[Route("api/v1/devices/shared")]
public sealed class DevicesController : ControllerBase
{
    [HttpPost]
    [HasPermission(Permissions.DevicesManage)]
    public async Task<ActionResult<RegisteredSharedDeviceDto>> Register(
        [FromBody] RegisterSharedDeviceCommand command,
        [FromServices] ICommandHandler<RegisterSharedDeviceCommand, RegisteredSharedDeviceDto> handler, CancellationToken cancellationToken)
    {
        var device = await handler.HandleAsync(command, cancellationToken);
        return Created("/api/v1/devices/shared", device);
    }

    [HttpGet]
    [HasPermission(Permissions.DevicesManage)]
    public async Task<ActionResult<ItemsDto<SharedDeviceDto>>> List(
        [FromServices] IQueryHandler<ListSharedDevicesQuery, ItemsDto<SharedDeviceDto>> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new ListSharedDevicesQuery(), cancellationToken));

    [HttpPost("{id:guid}/revoke")]
    [HasPermission(Permissions.DevicesManage)]
    public async Task<IActionResult> Revoke(
        Guid id, [FromServices] ICommandHandler<RevokeSharedDeviceCommand, bool> handler, CancellationToken cancellationToken)
    {
        await handler.HandleAsync(new RevokeSharedDeviceCommand(id), cancellationToken);
        return NoContent();
    }

    /// Público: autenticado pelo segredo do aparelho e pelo tenant informado no header.
    [HttpGet("{installationId}/employees")]
    [AllowAnonymous]
    public async Task<ActionResult<DeviceEmployeesDto>> PinEmployees(
        string installationId,
        [FromHeader(Name = "X-Tenant-Id")] Guid? tenantId,
        [FromHeader(Name = "X-Device-Secret")] string? deviceSecret,
        [FromServices] IQueryHandler<ListPinEmployeesQuery, DeviceEmployeesDto> handler, CancellationToken cancellationToken) =>
        tenantId is { } id
            ? Ok(await handler.HandleAsync(new ListPinEmployeesQuery(id, installationId, deviceSecret), cancellationToken))
            : throw new BusinessException(ErrorCodes.InvalidDeviceSecret);
}
