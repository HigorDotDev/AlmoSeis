using AlmoSeis.Api.Infrastructure;
using AlmoSeis.Modules.Audit.Contracts;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Security;
using Microsoft.AspNetCore.Mvc;

namespace AlmoSeis.Api.Controllers;

/// Auditoria (06 §6.13).
[ApiController]
[Route("api/v1/audit-logs")]
public sealed class AuditLogsController : ControllerBase
{
    [HttpGet]
    [HasPermission(Permissions.AuditView)]
    public async Task<ActionResult<PageDto<AuditLogDto>>> List(
        [FromQuery] string? entityType,
        [FromQuery] Guid? entityId,
        [FromQuery] Guid? actorEmployeeId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? cursor,
        [FromServices] IQueryHandler<ListAuditLogsQuery, PageDto<AuditLogDto>> handler,
        CancellationToken cancellationToken,
        [FromQuery] int limit = 50) =>
        Ok(await handler.HandleAsync(new ListAuditLogsQuery(entityType, entityId, actorEmployeeId, from, to, limit, cursor), cancellationToken));
}
