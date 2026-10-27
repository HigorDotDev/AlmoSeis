using AlmoSeis.Api.Infrastructure;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlmoSeis.Api.Controllers;

/// Autenticação e sessão (06 §6.3). Controller fino: monta o comando e chama o handler.
[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController : ControllerBase
{
    [HttpPost("signup")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResultDto>> SignUp(
        [FromBody] SignUpCommand command,
        [FromServices] ICommandHandler<SignUpCommand, AuthResultDto> handler,
        CancellationToken cancellationToken) =>
        Created("/api/v1/tenant", await handler.HandleAsync(command, cancellationToken));

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResultDto>> Login(
        [FromBody] LoginCommand command,
        [FromServices] ICommandHandler<LoginCommand, AuthOutcome<AuthResultDto>> handler,
        CancellationToken cancellationToken) =>
        Unwrap(await handler.HandleAsync(command, cancellationToken));

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<ActionResult<TokensDto>> Refresh(
        [FromBody] RefreshTokenCommand command,
        [FromServices] ICommandHandler<RefreshTokenCommand, AuthOutcome<TokensDto>> handler,
        CancellationToken cancellationToken) =>
        Unwrap(await handler.HandleAsync(command, cancellationToken));

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout(
        [FromBody] LogoutCommand command,
        [FromServices] ICommandHandler<LogoutCommand, bool> handler,
        CancellationToken cancellationToken)
    {
        await handler.HandleAsync(command, cancellationToken);
        return NoContent();
    }

    [HttpPost("switch-tenant")]
    [Authorize]
    public async Task<ActionResult<AuthResultDto>> SwitchTenant(
        [FromBody] SwitchTenantCommand command,
        [FromServices] ICommandHandler<SwitchTenantCommand, AuthResultDto> handler,
        CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(command, cancellationToken));

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<MeDto>> Me(
        [FromServices] IQueryHandler<GetMeQuery, MeDto> handler,
        CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new GetMeQuery(), cancellationToken));

    [HttpPut("me/pin")]
    [Authorize]
    public async Task<IActionResult> SetPin(
        [FromBody] SetOwnPinCommand command,
        [FromServices] ICommandHandler<SetOwnPinCommand, bool> handler,
        CancellationToken cancellationToken)
    {
        await handler.HandleAsync(command, cancellationToken);
        return NoContent();
    }

    [HttpPost("password/reset")]
    [AllowAnonymous]
    public async Task<IActionResult> ResetPassword(
        [FromBody] ResetPasswordCommand command,
        [FromServices] ICommandHandler<ResetPasswordCommand, bool> handler,
        CancellationToken cancellationToken)
    {
        await handler.HandleAsync(command, cancellationToken);
        return NoContent();
    }

    /// A falha só vira erro aqui, depois do commit do contador de tentativas / revogação.
    private static ActionResult<T> Unwrap<T>(AuthOutcome<T> outcome)
        where T : class =>
        outcome.Value ?? throw new BusinessException(
            ErrorCodes.All.Single(c => c.Code == outcome.FailureCode),
            extensions: outcome.FailureExtensions);
}

[ApiController]
[Route("api/v1/roles")]
[Authorize]
public sealed class RolesController : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<object>> List(
        [FromServices] IQueryHandler<ListRolesQuery, IReadOnlyList<RoleDto>> handler,
        CancellationToken cancellationToken) =>
        Ok(new { items = await handler.HandleAsync(new ListRolesQuery(), cancellationToken) });
}
