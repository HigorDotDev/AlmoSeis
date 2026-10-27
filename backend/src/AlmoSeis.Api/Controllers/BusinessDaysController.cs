using AlmoSeis.Api.Infrastructure;
using AlmoSeis.Modules.Closing.Contracts;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Security;
using Microsoft.AspNetCore.Mvc;

namespace AlmoSeis.Api.Controllers;

/// Expediente (06 §6.6).
[ApiController]
[Route("api/v1/business-days")]
public sealed class BusinessDaysController : ControllerBase
{
    [HttpGet("current")]
    [HasPermission(Permissions.Orders.View)]
    public async Task<ActionResult<BusinessDayDto>> GetCurrent(
        [FromServices] IQueryHandler<GetCurrentBusinessDayQuery, BusinessDayDto> handler,
        CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new GetCurrentBusinessDayQuery(), cancellationToken));

    [HttpPost]
    [HasPermission(Permissions.Cash.Open)]
    [Idempotent]
    public async Task<ActionResult<OpenBusinessDayResultDto>> Open(
        [FromBody] OpenBusinessDayCommand command,
        [FromServices] ICommandHandler<OpenBusinessDayCommand, OpenBusinessDayResultDto> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(command, cancellationToken);
        return Created("/api/v1/business-days/current", result);
    }

    [HttpGet]
    [HasPermission(Permissions.Closing.View)]
    public async Task<ActionResult<PageDto<BusinessDayDto>>> List(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] string? cursor,
        [FromServices] IQueryHandler<ListBusinessDaysQuery, PageDto<BusinessDayDto>> handler,
        CancellationToken cancellationToken,
        [FromQuery] int limit = 50) =>
        Ok(await handler.HandleAsync(new ListBusinessDaysQuery(from, to, limit, cursor), cancellationToken));
}
