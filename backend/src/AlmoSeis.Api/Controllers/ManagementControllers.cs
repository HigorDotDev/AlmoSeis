using AlmoSeis.Api.Infrastructure;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Security;
using Microsoft.AspNetCore.Mvc;

namespace AlmoSeis.Api.Controllers;

/// Estabelecimento e configurações (06 §6.4).
[ApiController]
[Route("api/v1/tenant")]
public sealed class TenantController : ControllerBase
{
    [HttpGet]
    [HasPermission(Permissions.Orders.View)]
    public async Task<ActionResult<TenantDto>> Get(
        [FromServices] IQueryHandler<GetTenantQuery, TenantDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new GetTenantQuery(), cancellationToken));

    [HttpPatch]
    [HasPermission(Permissions.SettingsManage)]
    [RequiresIfMatch]
    public async Task<ActionResult<TenantDto>> Update(
        [FromBody] UpdateTenantRequest request,
        [FromServices] ICommandHandler<UpdateTenantCommand, TenantDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new UpdateTenantCommand(request), cancellationToken));

    [HttpGet("settings")]
    [HasPermission(Permissions.Orders.View)]
    public async Task<ActionResult<TenantSettingsDto>> GetSettings(
        [FromServices] IQueryHandler<GetTenantSettingsQuery, TenantSettingsDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new GetTenantSettingsQuery(), cancellationToken));

    [HttpPatch("settings")]
    [HasPermission(Permissions.SettingsManage)]
    [RequiresIfMatch]
    public async Task<ActionResult<TenantSettingsDto>> UpdateSettings(
        [FromBody] UpdateTenantSettingsRequest request,
        [FromServices] ICommandHandler<UpdateTenantSettingsCommand, TenantSettingsDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new UpdateTenantSettingsCommand(request), cancellationToken));
}

/// Funcionários e convites (06 §6.4).
[ApiController]
[Route("api/v1/employees")]
public sealed class EmployeesController : ControllerBase
{
    [HttpGet]
    [HasPermission(Permissions.EmployeesManage)]
    public async Task<ActionResult<ItemsDto<EmployeeDto>>> List(
        [FromQuery] string? status,
        [FromServices] IQueryHandler<ListEmployeesQuery, ItemsDto<EmployeeDto>> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new ListEmployeesQuery(status), cancellationToken));

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.EmployeesManage)]
    public async Task<ActionResult<EmployeeDto>> Get(
        Guid id, [FromServices] IQueryHandler<GetEmployeeQuery, EmployeeDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new GetEmployeeQuery(id), cancellationToken));

    [HttpPatch("{id:guid}")]
    [HasPermission(Permissions.EmployeesManage)]
    [RequiresIfMatch]
    public async Task<ActionResult<EmployeeDto>> Update(
        Guid id, [FromBody] UpdateEmployeeRequest request,
        [FromServices] ICommandHandler<UpdateEmployeeCommand, EmployeeDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new UpdateEmployeeCommand(id, request), cancellationToken));

    [HttpPost("{id:guid}/deactivate")]
    [HasPermission(Permissions.EmployeesManage)]
    [RequiresIfMatch]
    public async Task<ActionResult<EmployeeDto>> Deactivate(
        Guid id, [FromServices] ICommandHandler<DeactivateEmployeeCommand, EmployeeDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new DeactivateEmployeeCommand(id), cancellationToken));

    [HttpPost("{id:guid}/reactivate")]
    [HasPermission(Permissions.EmployeesManage)]
    [RequiresIfMatch]
    public async Task<ActionResult<EmployeeDto>> Reactivate(
        Guid id, [FromServices] ICommandHandler<ReactivateEmployeeCommand, EmployeeDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new ReactivateEmployeeCommand(id), cancellationToken));

    [HttpPost("{id:guid}/password-reset-code")]
    [HasPermission(Permissions.EmployeesManage)]
    public async Task<ActionResult<InvitationDto>> PasswordResetCode(
        Guid id, [FromServices] ICommandHandler<IssuePasswordResetCodeCommand, InvitationDto> handler, CancellationToken cancellationToken)
    {
        var invitation = await handler.HandleAsync(new IssuePasswordResetCodeCommand(id), cancellationToken);
        return Created($"/api/v1/employees/invitations?status=PENDING", invitation);
    }

    [HttpPost("invitations")]
    [HasPermission(Permissions.EmployeesManage)]
    public async Task<ActionResult<InvitationDto>> Invite(
        [FromBody] CreateInvitationCommand command,
        [FromServices] ICommandHandler<CreateInvitationCommand, InvitationDto> handler, CancellationToken cancellationToken)
    {
        var invitation = await handler.HandleAsync(command, cancellationToken);
        return Created($"/api/v1/invitations/{invitation.Code}", invitation);
    }

    [HttpGet("invitations")]
    [HasPermission(Permissions.EmployeesManage)]
    public async Task<ActionResult<ItemsDto<InvitationDto>>> Invitations(
        [FromQuery] string? status,
        [FromServices] IQueryHandler<ListInvitationsQuery, ItemsDto<InvitationDto>> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new ListInvitationsQuery(status), cancellationToken));

    [HttpPost("invitations/{id:guid}/revoke")]
    [HasPermission(Permissions.EmployeesManage)]
    public async Task<IActionResult> RevokeInvitation(
        Guid id, [FromServices] ICommandHandler<RevokeInvitationCommand, bool> handler, CancellationToken cancellationToken)
    {
        await handler.HandleAsync(new RevokeInvitationCommand(id), cancellationToken);
        return NoContent();
    }
}

/// Convite pelo código (público, 06 §6.4).
[ApiController]
[Route("api/v1/invitations")]
public sealed class InvitationsController : ControllerBase
{
    [HttpGet("{code}")]
    public async Task<ActionResult<PublicInvitationDto>> Get(
        string code, [FromServices] IQueryHandler<GetInvitationByCodeQuery, PublicInvitationDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new GetInvitationByCodeQuery(code), cancellationToken));

    [HttpPost("{code}/accept")]
    public async Task<ActionResult<AuthResultDto>> Accept(
        string code, [FromBody] AcceptInvitationRequest request,
        [FromServices] ICommandHandler<AcceptInvitationCommand, AuthResultDto> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new AcceptInvitationCommand(code, request), cancellationToken);
        return Created("/api/v1/auth/me", result);
    }
}
