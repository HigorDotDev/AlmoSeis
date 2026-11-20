using AlmoSeis.Api.Infrastructure;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

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

    /// Na web, o refresh vem do cookie e o novo vai no cookie (WebSessionCookieFilter); a falha apaga o cookie.
    [HttpPost("refresh")]
    [AllowAnonymous]
    [WebOriginCheck]
    public async Task<ActionResult<TokensDto>> Refresh(
        [FromBody] RefreshTokenCommand command,
        [FromServices] ICommandHandler<RefreshTokenCommand, AuthOutcome<TokensDto>> handler,
        CancellationToken cancellationToken)
    {
        var token = WebSession.RefreshToken(Request, command.RefreshToken, required: true);
        var outcome = await handler.HandleAsync(command with { RefreshToken = token }, cancellationToken);
        if (outcome.Value is null && WebSession.IsWeb(Request))
        {
            WebSession.ClearCookieOnResponse(Response, WebSession.RefreshCookie);
        }

        return Unwrap(outcome);
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    [WebOriginCheck]
    public async Task<IActionResult> Logout(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] LogoutCommand? command,
        [FromServices] ICommandHandler<LogoutCommand, bool> handler,
        CancellationToken cancellationToken)
    {
        var token = WebSession.RefreshToken(Request, command?.RefreshToken, required: false);
        await handler.HandleAsync(new LogoutCommand(token), cancellationToken);
        if (WebSession.IsWeb(Request))
        {
            WebSession.ClearCookie(Response, WebSession.RefreshCookie);
        }

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

    [HttpPut("me/password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword(
        [FromBody] ChangeOwnPasswordCommand command,
        [FromServices] ICommandHandler<ChangeOwnPasswordCommand, bool> handler,
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

    [HttpPost("admin/login")]
    [AllowAnonymous]
    public async Task<ActionResult<AdminSessionDto>> AdminLogin(
        [FromBody] AdminLoginCommand command,
        [FromServices] ICommandHandler<AdminLoginCommand, AuthOutcome<AdminSessionDto>> handler,
        CancellationToken cancellationToken) =>
        Unwrap(await handler.HandleAsync(command, cancellationToken));

    /// Na web, sem `X-Device-Secret`, o segredo vem do cookie: o sucesso o reemite (renova a validade) e o
    /// `InvalidDeviceSecret` o apaga.
    [HttpPost("pin-login")]
    [AllowAnonymous]
    [WebOriginCheck]
    public async Task<ActionResult<AuthResultDto>> PinLogin(
        [FromBody] PinLoginRequest request,
        [FromHeader(Name = "X-Device-Secret")] string? deviceSecret,
        [FromServices] ICommandHandler<PinLoginCommand, AuthOutcome<AuthResultDto>> handler,
        CancellationToken cancellationToken)
    {
        var (secret, fromCookie) = WebSession.DeviceSecret(Request, deviceSecret);
        var outcome = await handler.HandleAsync(new PinLoginCommand(request, secret), cancellationToken);
        if (fromCookie && outcome.Value is not null)
        {
            WebSession.SetDeviceSecretCookie(Response, secret!);
        }
        else if (fromCookie && outcome.FailureCode == ErrorCodes.InvalidDeviceSecret.Code)
        {
            WebSession.ClearCookieOnResponse(Response, WebSession.DeviceSecretCookie);
        }

        return Unwrap(outcome);
    }

    /// Lista de funcionários do aparelho compartilhado na web (06 §6.3 "App no navegador"): a mesma resposta de
    /// `GET /devices/shared/{installationId}/employees`, sob /auth para o cookie do segredo alcançar.
    [HttpGet("shared-device/employees")]
    [AllowAnonymous]
    [WebOriginCheck]
    public async Task<ActionResult<DeviceEmployeesDto>> SharedDeviceEmployees(
        [FromQuery] string? installationId,
        [FromHeader(Name = "X-Tenant-Id")] Guid? tenantId,
        [FromHeader(Name = "X-Device-Secret")] string? deviceSecret,
        [FromServices] IQueryHandler<ListPinEmployeesQuery, DeviceEmployeesDto> handler,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(installationId) || installationId.Length > 64)
        {
            throw BusinessException.Validation("installationId", "Informe o installationId.");
        }

        var (secret, fromCookie) = WebSession.DeviceSecret(Request, deviceSecret);
        if (fromCookie)
        {
            // Segredo inválido (aparelho revogado ou registrado de novo): o navegador sai do modo compartilhado.
            Response.OnStarting(() =>
            {
                if (Response.StatusCode == ErrorCodes.InvalidDeviceSecret.HttpStatus)
                {
                    WebSession.ClearCookie(Response, WebSession.DeviceSecretCookie);
                }

                return Task.CompletedTask;
            });
        }

        return tenantId is { } id
            ? Ok(await handler.HandleAsync(new ListPinEmployeesQuery(id, installationId, secret), cancellationToken))
            : throw new BusinessException(ErrorCodes.InvalidDeviceSecret);
    }

    /// "Deixar de usar como compartilhado" no navegador: só apaga o cookie do segredo. Revogar é `POST /devices/shared/{id}/revoke`.
    [HttpDelete("shared-device")]
    [AllowAnonymous]
    [WebOriginCheck]
    public IActionResult ForgetSharedDevice()
    {
        WebSession.ClearCookie(Response, WebSession.DeviceSecretCookie);
        return NoContent();
    }

    [HttpPost("password/forgot")]
    [AllowAnonymous]
    public async Task<IActionResult> ForgotPassword(
        [FromBody] ForgotPasswordCommand command,
        [FromServices] ICommandHandler<ForgotPasswordCommand, bool> handler,
        CancellationToken cancellationToken)
    {
        await handler.HandleAsync(command, cancellationToken);
        return Accepted();
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
