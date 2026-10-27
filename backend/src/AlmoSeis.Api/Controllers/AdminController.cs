using System.Security.Claims;
using AlmoSeis.Api.Infrastructure;
using AlmoSeis.Modules.Billing.Contracts;
using AlmoSeis.SharedKernel.Application;
using Microsoft.AspNetCore.Mvc;

namespace AlmoSeis.Api.Controllers;

/// Administração da plataforma (06 §6.15). Só tokens de `POST /auth/admin/login` (ADR-0043).
[ApiController]
[Route("api/v1/admin")]
[PlatformAdmin]
public sealed class AdminController(IPlatformAdminService admin) : ControllerBase
{
    private Guid AdminUserId => Guid.Parse(User.FindFirstValue("sub")!);

    [HttpGet("tenants")]
    public async Task<ActionResult<PageDto<AdminTenantDto>>> Tenants(
        [FromQuery] string? status, [FromQuery] string? search, [FromQuery] string? cursor, CancellationToken cancellationToken,
        [FromQuery] int limit = 50) =>
        Ok(await admin.ListTenantsAsync(status, search, limit, cursor, cancellationToken));

    [HttpGet("tenants/{id:guid}")]
    public async Task<ActionResult<AdminTenantDetailDto>> Tenant(Guid id, CancellationToken cancellationToken) =>
        Ok(await admin.GetTenantAsync(id, cancellationToken));

    [HttpPost("tenants/{id:guid}/extend-trial")]
    public async Task<ActionResult<SubscriptionDto>> ExtendTrial(Guid id, [FromBody] ExtendTrialRequest request, CancellationToken cancellationToken) =>
        Ok(await admin.ExtendTrialAsync(AdminUserId, id, request, cancellationToken));

    [HttpPost("tenants/{id:guid}/suspend")]
    public async Task<ActionResult<SubscriptionDto>> Suspend(Guid id, [FromBody] AdminReasonRequest request, CancellationToken cancellationToken) =>
        Ok(await admin.SuspendAsync(AdminUserId, id, request, cancellationToken));

    [HttpPost("tenants/{id:guid}/reactivate")]
    public async Task<ActionResult<SubscriptionDto>> Reactivate(Guid id, [FromBody] AdminReasonRequest request, CancellationToken cancellationToken) =>
        Ok(await admin.ReactivateAsync(AdminUserId, id, request, cancellationToken));

    [HttpPost("billing-webhooks/{id:guid}/reprocess")]
    public async Task<IActionResult> Reprocess(Guid id, CancellationToken cancellationToken)
    {
        await admin.ReprocessWebhookAsync(AdminUserId, id, cancellationToken);
        return Accepted();
    }
}
