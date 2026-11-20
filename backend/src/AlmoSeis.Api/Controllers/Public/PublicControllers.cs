using AlmoSeis.Api.Infrastructure;
using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.Modules.Storefront.Contracts;
using AlmoSeis.SharedKernel.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlmoSeis.Api.Controllers.Public;

/// Página pública do restaurante (06 §6.19, ADR-0045). Sem JWT: o tenant vem do `{slug}`, resolvido pelo
/// `PublicTenantMiddleware`. Só chama handlers `IPublicHandler` (regra de arquitetura 9). DTOs próprios da página.
[ApiController]
[AllowAnonymous]
[Route("api/v1/public/stores/{slug}")]
public sealed class PublicStoresController : ControllerBase
{
    /// Corpo máximo do envio (ADR-0045 item 13).
    public const int MaxOrderBodyBytes = 32 * 1024;

    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult<PublicStoreDto>> Get(
        string slug, [FromServices] IQueryHandler<GetPublicStoreQuery, PublicStoreDto> handler, CancellationToken cancellationToken)
    {
        var store = await handler.HandleAsync(new GetPublicStoreQuery(slug), cancellationToken);
        // Estado e disponibilidade mudam a qualquer momento (pausa, "Acabou"): o servidor da página revalida sempre.
        Response.Headers.CacheControl = "no-cache";
        return Ok(store);
    }

    [HttpPost("orders")]
    [AllowAnonymous]
    [Idempotent(Public = true)]
    [RequestSizeLimit(MaxOrderBodyBytes)]
    public async Task<ActionResult<PlacedWebOrderDto>> PlaceOrder(
        string slug,
        [FromBody] PlaceWebOrderRequest request,
        [FromHeader(Name = PublicRoutes.TurnstileHeader)] string? turnstileToken,
        [FromServices] ICommandHandler<PlaceWebOrderCommand, PlacedWebOrderDto> handler,
        CancellationToken cancellationToken)
    {
        var placed = await handler.HandleAsync(
            new PlaceWebOrderCommand(request, turnstileToken, HttpContext.Connection.RemoteIpAddress?.ToString()), cancellationToken);
        return Created($"{PublicRoutes.Tracking}/{placed.TrackingToken}", placed);
    }

    /// Cotação da taxa por distância (ADR-0048). Nesta fatia responde `422 DeliveryUnavailable`.
    [HttpPost("delivery-quotes")]
    [AllowAnonymous]
    public async Task<ActionResult<DeliveryQuoteDto>> QuoteDelivery(
        string slug,
        [FromBody] DeliveryQuoteRequest request,
        [FromServices] ICommandHandler<DeliveryQuoteCommand, DeliveryQuoteDto> handler,
        CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new DeliveryQuoteCommand(request), cancellationToken));
}

/// Acompanhamento pelo cliente (06 §6.19, ADR-0047). O tenant vem do token (só o hash é consultado).
[ApiController]
[AllowAnonymous]
[Route("api/v1/public/tracking/{trackingToken}")]
public sealed class PublicTrackingController : ControllerBase
{
    /// Consulta a cada 20 s com `If-None-Match`: sem mudança → `304` (ADR-0047 item 8).
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Get(
        string trackingToken,
        [FromServices] IQueryHandler<GetWebOrderTrackingQuery, WebOrderTrackingDto> handler,
        CancellationToken cancellationToken)
    {
        var tracking = await handler.HandleAsync(new GetWebOrderTrackingQuery(trackingToken), cancellationToken);
        var etag = $"\"{tracking.Version}\"";
        if (Request.Headers.IfNoneMatch.Any(v => string.Equals(v?.Trim(), etag, StringComparison.Ordinal)))
        {
            Response.Headers.ETag = etag;
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(tracking);
    }

    [HttpPost("withdraw")]
    [AllowAnonymous]
    public async Task<ActionResult<WebOrderTrackingDto>> Withdraw(
        string trackingToken,
        [FromServices] ICommandHandler<WithdrawWebOrderByTrackingCommand, WebOrderTrackingDto> handler,
        CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new WithdrawWebOrderByTrackingCommand(trackingToken), cancellationToken));
}
