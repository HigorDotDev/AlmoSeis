using AlmoSeis.Api.Infrastructure;
using AlmoSeis.Modules.Closing.Contracts;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Security;
using Microsoft.AspNetCore.Mvc;

namespace AlmoSeis.Api.Controllers;

/// Caixa e fechamento (06 §6.11).
[ApiController]
[Route("api/v1/closing")]
public sealed class ClosingController : ControllerBase
{
    [HttpPost("cash-movements")]
    [HasPermission(Permissions.Cash.Movement)]
    [Idempotent]
    public async Task<ActionResult<CashMovementDto>> RegisterMovement(
        [FromBody] RegisterCashMovementCommand command,
        [FromServices] ICommandHandler<RegisterCashMovementCommand, CashMovementDto> handler, CancellationToken cancellationToken)
    {
        var movement = await handler.HandleAsync(command, cancellationToken);
        return Created($"/api/v1/closing/cash-movements?businessDayId={movement.BusinessDayId}", movement);
    }

    [HttpGet("cash-movements")]
    [HasPermission(Permissions.Closing.View)]
    public async Task<ActionResult<ItemsDto<CashMovementDto>>> ListMovements(
        [FromQuery] Guid? businessDayId,
        [FromServices] IQueryHandler<ListCashMovementsQuery, ItemsDto<CashMovementDto>> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new ListCashMovementsQuery(businessDayId), cancellationToken));

    [HttpGet("summary")]
    [HasPermission(Permissions.Closing.View)]
    public async Task<ActionResult<ClosingSummaryDto>> Summary(
        [FromQuery] Guid? businessDayId,
        [FromServices] IQueryHandler<GetClosingSummaryQuery, ClosingSummaryDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new GetClosingSummaryQuery(businessDayId), cancellationToken));

    [HttpPost("finalize")]
    [HasPermission(Permissions.Closing.Finalize)]
    [Idempotent]
    [RequiresIfMatch]
    public async Task<ActionResult<ClosingSummaryDto>> Finalize(
        [FromBody] FinalizeClosingCommand command,
        [FromServices] ICommandHandler<FinalizeClosingCommand, ClosingSummaryDto> handler, CancellationToken cancellationToken)
    {
        var closing = await handler.HandleAsync(command, cancellationToken);
        return Created($"/api/v1/closing/{closing.DailyClosingId}", closing);
    }

    [HttpGet("history")]
    [HasPermission(Permissions.Closing.View)]
    public async Task<ActionResult<PageDto<ClosingHistoryItemDto>>> History(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? cursor,
        [FromServices] IQueryHandler<ListClosingHistoryQuery, PageDto<ClosingHistoryItemDto>> handler,
        CancellationToken cancellationToken, [FromQuery] int limit = 31) =>
        Ok(await handler.HandleAsync(new ListClosingHistoryQuery(from, to, limit, cursor), cancellationToken));

    [HttpGet("monthly")]
    [HasPermission(Permissions.Closing.View)]
    public async Task<ActionResult<MonthlyClosingDto>> Monthly(
        [FromQuery] string month,
        [FromServices] IQueryHandler<GetMonthlyClosingQuery, MonthlyClosingDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new GetMonthlyClosingQuery(month), cancellationToken));

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.Closing.View)]
    public async Task<ActionResult<ClosingSummaryDto>> Get(
        Guid id, [FromServices] IQueryHandler<GetClosingQuery, ClosingSummaryDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new GetClosingQuery(id), cancellationToken));

    [HttpPost("{id:guid}/adjustments")]
    [HasPermission(Permissions.Closing.Adjust)]
    [Idempotent]
    public async Task<ActionResult<ClosingAdjustmentDto>> Adjust(
        Guid id, [FromBody] CreateClosingAdjustmentRequest request,
        [FromServices] ICommandHandler<CreateClosingAdjustmentCommand, ClosingAdjustmentDto> handler, CancellationToken cancellationToken)
    {
        var adjustment = await handler.HandleAsync(new CreateClosingAdjustmentCommand(id, request), cancellationToken);
        return Created($"/api/v1/closing/{id}", adjustment);
    }
}
