using AlmoSeis.Api.Infrastructure;
using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.Modules.Storefront.Contracts;
using AlmoSeis.SharedKernel.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

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

    /// O Turnstile e a conferência da cotação rodam antes da transação da idempotência (`PlaceWebOrderPreparation`, 07 §7.4).
    [HttpPost("orders")]
    [AllowAnonymous]
    [Idempotent(Public = true, ReplaySecret = "trackingToken", Preparation = typeof(PlaceWebOrderPreparation))]
    [RequestSizeLimit(MaxOrderBodyBytes)]
    public async Task<ActionResult<PlacedWebOrderDto>> PlaceOrder(
        string slug,
        [FromBody] PlaceWebOrderRequest request,
        [FromHeader(Name = PublicRoutes.TurnstileHeader)] string? turnstileToken,
        [FromServices] ICommandHandler<PlaceWebOrderCommand, PlacedWebOrderDto> handler,
        CancellationToken cancellationToken)
    {
        var prepared = HttpContext.Items[PlaceWebOrderPreparation.ItemKey] as WebOrderPreparation;
        var placed = await handler.HandleAsync(
            new PlaceWebOrderCommand(request, turnstileToken, HttpContext.Connection.RemoteIpAddress?.ToString(), prepared), cancellationToken);
        return Created($"{PublicRoutes.Tracking}/{placed.TrackingToken}", placed);
    }

    /// Fora da transação, chamado pelo filtro de idempotência depois de conferir que não é reenvio.
    [NonAction]
    public static async Task PrepareOrderAsync(
        ActionExecutingContext context, IQueryHandler<PrepareWebOrderQuery, WebOrderPreparation> handler, CancellationToken cancellationToken)
    {
        if (context.ActionArguments.TryGetValue("request", out var value) && value is PlaceWebOrderRequest request)
        {
            var http = context.HttpContext;
            var token = http.Request.Headers[PublicRoutes.TurnstileHeader].FirstOrDefault();
            http.Items[PlaceWebOrderPreparation.ItemKey] = await handler.HandleAsync(
                new PrepareWebOrderQuery(request, token, http.Connection.RemoteIpAddress?.ToString()), cancellationToken);
        }
    }

    /// Cotação da taxa de entrega (ADR-0048 item 8): `201` com a cotação de 15 min. Turnstile como no envio (ADR-0045 item 12).
    [HttpPost("delivery-quotes")]
    [AllowAnonymous]
    public async Task<ActionResult<DeliveryQuoteDto>> QuoteDelivery(
        string slug,
        [FromBody] DeliveryQuoteRequest request,
        [FromHeader(Name = PublicRoutes.TurnstileHeader)] string? turnstileToken,
        [FromServices] ICommandHandler<DeliveryQuoteCommand, DeliveryQuoteDto> handler,
        CancellationToken cancellationToken)
    {
        var quote = await handler.HandleAsync(
            new DeliveryQuoteCommand(request, turnstileToken, HttpContext.Connection.RemoteIpAddress?.ToString()), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, quote);
    }
}

/// `IIdempotentPreparation` do envio pela página: o `PrepareWebOrderQuery` (Turnstile + cotação) vai para `HttpContext.Items`.
internal sealed class PlaceWebOrderPreparation(IQueryHandler<PrepareWebOrderQuery, WebOrderPreparation> handler) : IIdempotentPreparation
{
    public const string ItemKey = "public:web-order-preparation";

    public Task PrepareAsync(ActionExecutingContext context, CancellationToken cancellationToken) =>
        PublicStoresController.PrepareOrderAsync(context, handler, cancellationToken);
}

/// Acompanhamento pelo cliente (06 §6.19, ADR-0047). O tenant vem do token (só o hash é consultado).
[ApiController]
[AllowAnonymous]
[Route("api/v1/public/tracking/{trackingToken}")]
public sealed class PublicTrackingController : ControllerBase
{
    /// Consulta a cada 20 s com `If-None-Match`: sem mudança → `304` (ADR-0047 item 8). O `ETag` combina a versão do
    /// pedido, o status calculado e a configuração da página (`WebOrderTrackingDto.ETag`).
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Get(
        string trackingToken,
        [FromServices] IQueryHandler<GetWebOrderTrackingQuery, WebOrderTrackingDto> handler,
        CancellationToken cancellationToken)
    {
        var tracking = await handler.HandleAsync(new GetWebOrderTrackingQuery(trackingToken), cancellationToken);
        Response.Headers.ETag = tracking.ETag;
        if (Request.Headers.IfNoneMatch.Any(v => string.Equals(v?.Trim(), tracking.ETag, StringComparison.Ordinal)))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(tracking);
    }

    /// Idempotente pelo estado (ADR-0046 item 17): repetir a desistência já feita devolve `200` com o acompanhamento.
    [HttpPost("withdraw")]
    [AllowAnonymous]
    public async Task<ActionResult<WebOrderTrackingDto>> Withdraw(
        string trackingToken,
        [FromServices] ICommandHandler<WithdrawWebOrderByTrackingCommand, WebOrderTrackingDto> handler,
        CancellationToken cancellationToken)
    {
        var tracking = await handler.HandleAsync(new WithdrawWebOrderByTrackingCommand(trackingToken), cancellationToken);
        Response.Headers.ETag = tracking.ETag;
        return Ok(tracking);
    }
}
