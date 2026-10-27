using AlmoSeis.Modules.Sync.Contracts;
using AlmoSeis.SharedKernel.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlmoSeis.Api.Controllers;

/// Sincronização e operações (06 §6.12): qualquer funcionário autenticado; o filtro por permissão é do handler.
[ApiController]
[Authorize]
public sealed class SyncController : ControllerBase
{
    [HttpGet("api/v1/sync/changes")]
    public async Task<ActionResult<SyncChangesDto>> Changes(
        [FromServices] IQueryHandler<GetSyncChangesQuery, SyncChangesDto> handler,
        CancellationToken cancellationToken, [FromQuery] long since = 0, [FromQuery] int limit = 500) =>
        Ok(await handler.HandleAsync(new GetSyncChangesQuery(since, limit), cancellationToken));

    [HttpGet("api/v1/operations/{idempotencyKey:guid}")]
    public async Task<ActionResult<OperationDto>> Operation(
        Guid idempotencyKey, [FromServices] IQueryHandler<GetOperationQuery, OperationDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new GetOperationQuery(idempotencyKey), cancellationToken));
}
