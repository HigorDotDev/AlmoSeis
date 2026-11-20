using AlmoSeis.Api.Infrastructure;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.Modules.Notifications.Contracts;
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
        // Na web o segredo vai em cookie HttpOnly (ADR-0053); `platform` e `X-App-Platform` precisam concordar.
        if ((command.Platform == WebSession.WebPlatform) != WebSession.IsWeb(Request))
        {
            throw BusinessException.Validation("platform", "Use WEB só no navegador, com o header X-App-Platform: WEB.");
        }

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

/// Registro do aparelho para o push dos pedidos do site (06 §6.3, ADR-0049 item 4). Qualquer perfil autenticado: quem
/// recebe é decidido no envio, pelas permissões atuais (`orders.accept`). Fora do bloqueio da assinatura, como `/auth`.
[ApiController]
[Route("api/v1/devices/push-token")]
[Authorize]
public sealed class PushTokenController : ControllerBase
{
    [HttpPut]
    public async Task<ActionResult<PushRegistrationDto>> Register(
        [FromBody] RegisterPushTokenCommand command,
        [FromServices] ICommandHandler<RegisterPushTokenCommand, PushRegistrationDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(command, cancellationToken));

    [HttpDelete]
    public async Task<IActionResult> Remove(
        [FromServices] ICommandHandler<RemovePushTokenCommand, bool> handler, CancellationToken cancellationToken)
    {
        await handler.HandleAsync(new RemovePushTokenCommand(), cancellationToken);
        return NoContent();
    }
}
