using AlmoSeis.Infrastructure.Audit;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Security;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.Modules.Ordering.Domain;
using AlmoSeis.Modules.Storefront.Contracts;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AlmoSeis.Modules.Ordering.Application;

/// POST /public/stores/{slug}/orders [I] (06 §6.19; ADR-0044, ADR-0045, ADR-0046). O tenant vem do slug, resolvido no
/// servidor; o idempotência é anônima, com escopo `(tenant, key)`. Ordem das barreiras: campo-isca → estado da página →
/// Turnstile → modalidade → forma de pagamento → pedidos pendentes por telefone e IP → carrinho recalculado → gravação.
internal sealed class PlaceWebOrderHandler(
    AppDbContext db,
    ITenantContext tenant,
    IStorefrontGate storefront,
    ITenantSettingsReader settings,
    ITurnstileVerifier turnstile,
    IClientIpHasher ipHasher,
    ISecretHasher hasher,
    ITrackingTokenIssuer trackingTokens,
    WebOrderSubmitter submitter,
    IDeliveryQuoteRedeemer quotes,
    TimeProvider time) : ICommandHandler<PlaceWebOrderCommand, PlacedWebOrderDto>, IPublicHandler
{
    /// ADR-0045 item 13.
    public const int MaxPendingPerPhone = 2;
    public const int MaxPendingPerIp = 3;

    public async Task<PlacedWebOrderDto> HandleAsync(PlaceWebOrderCommand command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (!string.IsNullOrEmpty(request.Website))
        {
            // Campo-isca preenchido: robô. 400 sem detalhes (ADR-0045 item 13).
            throw new BusinessException(ErrorCodes.MalformedRequest);
        }

        var store = await storefront.GetAsync(cancellationToken);
        if (store.Status != StoreStatus.Open || store.BusinessDate is not { } businessDate)
        {
            throw StoreErrors.Unavailable(store.Status);
        }

        var captcha = command.Preparation?.Captcha ?? await turnstile.VerifyAsync(command.TurnstileToken, command.ClientIp, cancellationToken);
        if (captcha == TurnstileOutcome.Failed)
        {
            throw new BusinessException(ErrorCodes.CaptchaFailed);
        }

        if (request.FulfillmentType == FulfillmentType.Delivery && !store.DeliveryAvailable)
        {
            // Entrega pausada ou sem o que o ADR-0048 exige (ponto confirmado, preço por km, provedor): só retirada.
            throw new BusinessException(ErrorCodes.DeliveryUnavailable);
        }

        if (request.FulfillmentType == FulfillmentType.Pickup && !store.PickupAvailable)
        {
            throw BusinessException.Validation("fulfillmentType", "Este restaurante não faz retirada pela página.");
        }

        var method = UpperSnakeEnumConverter<ExpectedPaymentMethod>.ToText(request.ExpectedPaymentMethod);
        if (!(await settings.GetAsync(cancellationToken)).EnabledPaymentMethods.Contains(method))
        {
            throw new BusinessException(ErrorCodes.PaymentMethodDisabled);
        }

        var phone = BrazilianPhone.Normalize(request.CustomerPhone)
            ?? throw BusinessException.Validation("customerPhone", BrazilianPhone.Message);
        var now = time.GetUtcNow();
        var ipHash = ipHasher.Hash(command.ClientIp, now);
        await EnsurePendingLimitsAsync(phone, ipHash, now, cancellationToken);

        var menu = await storefront.GetMenuAsync(businessDate, cancellationToken)
            ?? throw new BusinessException(
                ErrorCodes.MenuNotFound, "O cardápio de hoje ainda não foi publicado.",
                new Dictionary<string, object?> { ["menuType"] = "MARMITA", ["businessDate"] = businessDate });
        // Entrega: a taxa é a da cotação que o cliente viu, conferida aqui (ADR-0048 item 10). Taxa mudada → 409 com a nova.
        var deliveryFee = Money.Zero;
        DeliveryPricing? pricing = null;
        DeliveryAddressDto? address = null;
        if (request.FulfillmentType == FulfillmentType.Delivery && request.DeliveryAddress is { } input)
        {
            var redeemed = command.Preparation is { QuoteChecked: true } prepared
                ? prepared.Quote ?? throw prepared.QuoteError!
                : await quotes.RedeemAsync(request.DeliveryQuoteId, ToAddress(input), cancellationToken);
            deliveryFee = redeemed.DeliveryFee;
            pricing = redeemed.FeeMode == DeliveryFeeMode.Distance
                ? new DeliveryPricing(DeliveryFeeSource.Distance, redeemed.DistanceMeters, redeemed.PricePerKm, redeemed.MinimumFee)
                : new DeliveryPricing(DeliveryFeeSource.FixedDefault);
            address = new DeliveryAddressDto(input.Street.Trim(), Clean(input.Number), Clean(input.Complement), Clean(input.Neighborhood), Clean(input.Reference));
        }

        var cart = WebOrderCart.Price(request.Items, menu, deliveryFee);
        if (cart.Items.Any(i => !i.Available) || cart.TotalAmount != request.ExpectedTotal)
        {
            throw new BusinessException(ErrorCodes.CartChanged, extensions: new Dictionary<string, object?> { ["cart"] = cart });
        }

        var order = await submitter.SubmitAsync(
            new SubmitWebOrderCommand(
                request.FulfillmentType, request.CustomerName.Trim(), phone, address, deliveryFee, Clean(request.Notes),
                request.Items.Select(ToOrderItem).ToList(),
                request.ExpectedPaymentMethod, request.ChangeFor,
                captcha == TurnstileOutcome.Unavailable ? WebOrderRiskFlag.CaptchaUnavailable : null,
                pricing),
            cancellationToken);

        // O pedido gravado precisa ter exatamente o total e a taxa que o cliente confirmou (ADR-0044 item 21): o carrinho
        // da página e o resolvedor de itens do pedido são cálculos separados, e uma divergência entre eles nunca vira venda.
        if (order.TotalAmount != cart.TotalAmount || order.DeliveryFee != deliveryFee || order.ItemsTotal != cart.ItemsTotal)
        {
            throw new BusinessException(ErrorCodes.CartChanged, extensions: new Dictionary<string, object?> { ["cart"] = cart });
        }

        var tenantId = tenant.TenantId!.Value;
        // Derivado do pedido (ADR-0047): o reenvio idempotente recalcula o mesmo token, que não fica guardado em claro.
        var token = trackingTokens.For(tenantId, order.Id);
        var tracking = OrderTrackingToken.Issue(hasher.Hash(token), tenantId, order.Id, now);
        db.Add(tracking);
        db.Add(WebOrderIntake.Record(order.Id, tenantId, ipHash, now));

        return new PlacedWebOrderDto(
            token, tracking.ExpiresAt, order.AcceptanceStatus, order.AcceptanceDeadline!.Value, order.ItemsTotal, order.DeliveryFee, order.TotalAmount)
        {
            OrderId = order.Id,
        };
    }

    /// No máximo 2 pedidos aguardando aceite por telefone e 3 por IP, por tenant (ADR-0045 item 13). A trava por telefone
    /// e por IP serializa dois envios simultâneos do mesmo cliente, para os dois não passarem pela contagem.
    private async Task EnsurePendingLimitsAsync(string phone, string ipHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId!.Value;
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"web-order-phone:" + tenantId + ":" + phone}, 0))", cancellationToken);
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"web-order-ip:" + tenantId + ":" + ipHash}, 0))", cancellationToken);

        var pending = db.Set<Order>().Where(o => o.AcceptanceStatus == AcceptanceStatus.AwaitingAcceptance && o.AcceptanceDeadline > now);
        if (await pending.CountAsync(o => o.CustomerPhone == phone, cancellationToken) >= MaxPendingPerPhone
            || await pending.CountAsync(o => db.Set<WebOrderIntake>().Any(i => i.OrderId == o.Id && i.IpHash == ipHash), cancellationToken) >= MaxPendingPerIp)
        {
            throw new BusinessException(ErrorCodes.TooManyPendingOrders);
        }
    }

    internal static PublicAddressInput ToAddress(PublicDeliveryAddressInput input) =>
        new(input.Street, input.Number, input.Complement, input.Neighborhood, input.City, input.ZipCode, input.Reference);

    private static OrderItemInput ToOrderItem(PublicOrderItemInput i) => new(
        i.ItemKind, null, i.MarmitaSizeId, i.ProductId, i.Quantity, i.MainDishProductIds, i.SideDishProductIds, Clean(i.Notes),
        i.Addons?.Select(a => new AddonInput(a.ProductId, a.Quantity, null)).ToList());

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

/// Pré-checagens do envio fora da transação (07 §7.4): o `IdempotencyFilter` chama pela rota pública depois de conferir
/// que não é reenvio. Página fora de `OPEN` ou campo-isca → nada (o handler responde o erro sem gastar o Turnstile nem o
/// mapa). Só os erros de negócio da cotação são guardados; os demais seguem como antes.
internal sealed class PrepareWebOrderQueryHandler(IStorefrontGate storefront, ITurnstileVerifier turnstile, IDeliveryQuoteRedeemer quotes)
    : IQueryHandler<PrepareWebOrderQuery, WebOrderPreparation>, IPublicHandler
{
    public async Task<WebOrderPreparation> HandleAsync(PrepareWebOrderQuery query, CancellationToken cancellationToken)
    {
        var request = query.Request;
        if (!string.IsNullOrEmpty(request.Website))
        {
            return WebOrderPreparation.None;
        }

        var store = await storefront.GetAsync(cancellationToken);
        if (store.Status != StoreStatus.Open)
        {
            return WebOrderPreparation.None;
        }

        var captcha = await turnstile.VerifyAsync(query.TurnstileToken, query.ClientIp, cancellationToken);
        if (captcha == TurnstileOutcome.Failed || request.FulfillmentType != FulfillmentType.Delivery || !store.DeliveryAvailable
            || request.DeliveryAddress is not { } input || request.DeliveryQuoteId is null)
        {
            return new WebOrderPreparation(captcha, null, null);
        }

        try
        {
            return new WebOrderPreparation(captcha, await quotes.RedeemAsync(request.DeliveryQuoteId, PlaceWebOrderHandler.ToAddress(input), cancellationToken), null);
        }
        catch (BusinessException ex)
        {
            return new WebOrderPreparation(captcha, null, ex);
        }
    }
}

/// Reenvio do `POST /public/stores/{slug}/orders`: o `trackingToken` não fica no corpo guardado (ADR-0047, só o hash) e é
/// recalculado a partir do pedido (`resource_id`), no tenant da sessão.
internal sealed class TrackingTokenReplaySource(ITrackingTokenIssuer tokens) : AlmoSeis.Infrastructure.Idempotency.IReplaySecretSource
{
    public const string TrackingTokenField = "trackingToken";

    public string Field => TrackingTokenField;

    public string Recreate(Guid tenantId, Guid resourceId) => tokens.For(tenantId, resourceId);
}

/// Preço do carrinho da página contra o cardápio público do dia (o mesmo que a página mostrou). Linha com tamanho, prato,
/// acompanhamento, adicional ou bebida fora do cardápio, indisponível ou escondido → indisponível (ADR-0044 item 21).
internal static class WebOrderCart
{
    public static CartQuoteDto Price(IReadOnlyList<PublicOrderItemInput> items, PublicMenuDto menu, Money deliveryFee)
    {
        var sizes = menu.MarmitaSizes.ToDictionary(s => s.Id);
        var mains = menu.MainDishes.ToDictionary(d => d.ProductId);
        var sides = menu.SideDishes.ToDictionary(d => d.ProductId);
        var addons = menu.Addons.ToDictionary(p => p.ProductId);
        var beverages = menu.Beverages.ToDictionary(p => p.ProductId);

        var lines = new List<CartLineDto>();
        var unavailable = new List<Guid>();
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var ok = true;
            string? name = null;
            var unit = Money.Zero;
            var addonsPerUnit = Money.Zero;

            if (item.ItemKind == OrderItemKind.Marmita)
            {
                if (item.MarmitaSizeId is { } sizeId && sizes.TryGetValue(sizeId, out var size))
                {
                    (name, unit) = ($"Marmita {size.Name}", size.Price);
                }
                else
                {
                    ok = false;
                }

                foreach (var id in item.MainDishProductIds ?? [])
                {
                    ok &= Dish(mains, id, unavailable);
                }

                foreach (var id in item.SideDishProductIds ?? [])
                {
                    ok &= Dish(sides, id, unavailable);
                }

                foreach (var addon in item.Addons ?? [])
                {
                    if (addons.TryGetValue(addon.ProductId, out var product) && product.IsAvailable)
                    {
                        addonsPerUnit += product.Price * addon.Quantity;
                    }
                    else
                    {
                        ok = false;
                        unavailable.Add(addon.ProductId);
                    }
                }
            }
            else if (item.ProductId is { } productId && beverages.TryGetValue(productId, out var beverage) && beverage.IsAvailable)
            {
                (name, unit) = (beverage.Name, beverage.Price);
            }
            else
            {
                ok = false;
                if (item.ProductId is { } missing)
                {
                    unavailable.Add(missing);
                }
            }

            lines.Add(ok
                ? new CartLineDto(index, name, true, unit, (unit + addonsPerUnit) * item.Quantity)
                : new CartLineDto(index, name, false, null, null));
        }

        var itemsTotal = Money.Sum(lines.Where(l => l.Available).Select(l => l.LineTotal!.Value));
        return new CartQuoteDto(lines, unavailable.Distinct().ToList(), itemsTotal, deliveryFee, itemsTotal + deliveryFee);
    }

    private static bool Dish(Dictionary<Guid, PublicDishDto> section, Guid id, List<Guid> unavailable)
    {
        if (section.TryGetValue(id, out var dish) && dish.IsAvailable)
        {
            return true;
        }

        unavailable.Add(id);
        return false;
    }
}

/// Telefone brasileiro digitado pelo cliente (ADR-0044 item 18) → E.164. Aceita "(34) 98888-7777", "34988887777",
/// "+55 34 98888-7777" e o DDD com zero ("034…"). Celular: 11 dígitos começando por 9 depois do DDD; fixo: 10 dígitos
/// começando por 2 a 5.
internal static class BrazilianPhone
{
    public const string Message = "Informe um telefone com DDD, ex.: (34) 99999-1234.";

    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 30 || input.Any(c => !(char.IsAsciiDigit(c) || c is ' ' or '(' or ')' or '-' or '+' or '.')))
        {
            return null;
        }

        var digits = new string(input.Where(char.IsAsciiDigit).ToArray());
        if (input.TrimStart().StartsWith('+') || (digits.StartsWith("55", StringComparison.Ordinal) && digits.Length is 12 or 13))
        {
            if (!digits.StartsWith("55", StringComparison.Ordinal))
            {
                return null;
            }

            digits = digits[2..];
        }
        else if (digits.StartsWith('0') && digits.Length is 11 or 12)
        {
            digits = digits[1..];
        }

        var valid = digits.Length switch
        {
            11 => digits[2] == '9',
            10 => digits[2] is >= '2' and <= '5',
            _ => false,
        };
        return valid && digits[0] != '0' && digits[1] != '0' ? "+55" + digits : null;
    }
}

/// Formato do envio (06 §6.19). Limites do ADR-0044 item 4 (decisão do usuário: 20 marmitas, sem pedido só de bebidas).
internal sealed class PlaceWebOrderRequestValidator : AbstractValidator<PlaceWebOrderRequest>
{
    public PlaceWebOrderRequestValidator()
    {
        RuleFor(x => x.CustomerName).Must(n => n is not null && n.Trim().Length is >= 2 and <= 80).WithMessage("Informe seu nome (2 a 80 caracteres).");
        RuleFor(x => x.CustomerPhone).Must(p => BrazilianPhone.Normalize(p) is not null).WithMessage(BrazilianPhone.Message);
        RuleFor(x => x.FulfillmentType).Must(f => f is FulfillmentType.Delivery or FulfillmentType.Pickup).WithMessage("Escolha entrega ou retirada.");
        RuleFor(x => x.DeliveryAddress).NotNull().WithMessage("Informe o endereço de entrega.").When(x => x.FulfillmentType == FulfillmentType.Delivery);
        RuleFor(x => x.DeliveryAddress).Null().WithMessage("Retirada não tem endereço de entrega.").When(x => x.FulfillmentType == FulfillmentType.Pickup);
        RuleFor(x => x.DeliveryQuoteId).NotNull().WithMessage("Calcule a taxa de entrega antes de enviar o pedido.").When(x => x.FulfillmentType == FulfillmentType.Delivery);
        RuleFor(x => x.DeliveryAddress!.Street).NotEmpty().MaximumLength(120).When(x => x.DeliveryAddress is not null);
        RuleFor(x => x.DeliveryAddress!.Number).MaximumLength(10).When(x => x.DeliveryAddress is not null);
        RuleFor(x => x.DeliveryAddress!.Complement).MaximumLength(60).When(x => x.DeliveryAddress is not null);
        RuleFor(x => x.DeliveryAddress!.Neighborhood).MaximumLength(60).When(x => x.DeliveryAddress is not null);
        RuleFor(x => x.DeliveryAddress!.City).MaximumLength(80).When(x => x.DeliveryAddress is not null);
        RuleFor(x => x.DeliveryAddress!.ZipCode).Matches(@"^[0-9]{5}-?[0-9]{3}$").When(x => x.DeliveryAddress is not null);
        RuleFor(x => x.DeliveryAddress!.Reference).MaximumLength(120).When(x => x.DeliveryAddress is not null);
        RuleFor(x => x.Notes).MaximumLength(PublicOrderLimits.NotesMaxLength);
        RuleFor(x => x.ChangeFor).Must(c => c is null || c.Value > Money.Zero).WithMessage("Informe um valor maior que zero.");
        RuleFor(x => x.ChangeFor).Null().WithMessage("Troco só para pagamento em dinheiro.").When(x => x.ExpectedPaymentMethod != ExpectedPaymentMethod.Cash);
        RuleFor(x => x.ExpectedTotal).Must(t => !t.IsNegative).WithMessage("Total inválido.");

        RuleFor(x => x.Items).NotEmpty().WithMessage("O pedido precisa de pelo menos 1 marmita.")
            .Must(items => items is null || items.Count <= PublicOrderLimits.MaxLines)
            .WithMessage($"Até {PublicOrderLimits.MaxLines} itens por pedido. Para mais, fale com o restaurante.")
            .Must(items => items is null || items.Count == 0 || items.Any(i => i.ItemKind == OrderItemKind.Marmita))
            .WithMessage("O pedido precisa de pelo menos 1 marmita.")
            .Must(items => items is null || items.Where(i => i.ItemKind == OrderItemKind.Marmita).Sum(i => (long)i.Quantity) <= PublicOrderLimits.MaxMarmitas)
            .WithMessage($"Até {PublicOrderLimits.MaxMarmitas} marmitas por pedido. Para mais, fale com o restaurante.");
        RuleForEach(x => x.Items).SetValidator(new PublicOrderItemValidator());
    }
}

internal sealed class PublicOrderItemValidator : AbstractValidator<PublicOrderItemInput>
{
    public PublicOrderItemValidator()
    {
        RuleFor(x => x.ItemKind).Must(k => k is OrderItemKind.Marmita or OrderItemKind.Beverage).WithMessage("A página vende só marmitas e bebidas.");
        RuleFor(x => x.Quantity).InclusiveBetween(1, PublicOrderLimits.MaxQuantityPerLine).WithMessage($"Deve ser entre 1 e {PublicOrderLimits.MaxQuantityPerLine}.");
        RuleFor(x => x.Notes).MaximumLength(PublicOrderLimits.NotesMaxLength);
        RuleFor(x => x.MarmitaSizeId).NotNull().WithMessage("Escolha o tamanho da marmita.").When(x => x.ItemKind == OrderItemKind.Marmita);
        RuleFor(x => x.MainDishProductIds).Must(m => m is { Count: >= 1 and <= 10 }).WithMessage("Escolha pelo menos um prato.").When(x => x.ItemKind == OrderItemKind.Marmita);
        RuleFor(x => x.SideDishProductIds).Must(s => s is null || s.Count <= 20).When(x => x.ItemKind == OrderItemKind.Marmita);
        RuleFor(x => x.Addons).Must(a => a is null || a.Count <= 10).When(x => x.ItemKind == OrderItemKind.Marmita);
        RuleForEach(x => x.Addons).ChildRules(a => a.RuleFor(y => y.Quantity).InclusiveBetween(1, PublicOrderLimits.MaxQuantityPerLine))
            .When(x => x.ItemKind == OrderItemKind.Marmita);
        RuleFor(x => x.ProductId).NotNull().WithMessage("Escolha a bebida.").When(x => x.ItemKind == OrderItemKind.Beverage);
        RuleFor(x => x.Addons).Must(a => a is null || a.Count == 0).WithMessage("Bebida não tem adicionais.").When(x => x.ItemKind == OrderItemKind.Beverage);
        RuleFor(x => x.MarmitaSizeId).Null().When(x => x.ItemKind == OrderItemKind.Beverage);
    }
}

/// Token → tenant para o middleware das rotas públicas (ADR-0045 item 4), antes de o tenant existir na sessão. Só pela
/// função `resolve_tracking_token` (05 §5.17 0007): a tabela não tem leitura aberta, e a função devolve só o tenant e o
/// prazo do hash pedido.
internal sealed class TrackingTokenResolver(AppDbContext db, ISecretHasher hasher) : ITrackingTokenResolver
{
    public async Task<TrackingTokenResolution?> ResolveAsync(string trackingToken, CancellationToken cancellationToken)
    {
        if (!TrackingTokens.LooksValid(trackingToken))
        {
            return null;
        }

        var hash = hasher.Hash(trackingToken);
        var rows = await db.Database
            .SqlQuery<TokenRow>($"SELECT tenant_id, expires_at FROM resolve_tracking_token({hash}::bpchar)")
            .ToListAsync(cancellationToken);
        return rows is [var row] ? new TrackingTokenResolution(row.TenantId, row.ExpiresAt) : null;
    }

    private sealed record TokenRow(Guid TenantId, DateTimeOffset ExpiresAt);
}

/// Pedido do token, no tenant já resolvido (query filter e RLS valem). Token inexistente → `404 NotFound` sem dizer
/// mais nada; vencido → `410 TrackingExpired` (ADR-0047 item 4).
internal sealed class TrackedOrderLoader(AppDbContext db, ISecretHasher hasher, TimeProvider time)
{
    public async Task<(Guid OrderId, DateTimeOffset ExpiresAt)> FindAsync(string trackingToken, CancellationToken cancellationToken)
    {
        if (!TrackingTokens.LooksValid(trackingToken))
        {
            throw new NotFoundException("ORDER");
        }

        var hash = hasher.Hash(trackingToken);
        var row = await db.Set<OrderTrackingToken>().AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken)
            ?? throw new NotFoundException("ORDER");
        if (row.ExpiresAt <= time.GetUtcNow())
        {
            throw new BusinessException(ErrorCodes.TrackingExpired);
        }

        return (row.OrderId, row.ExpiresAt);
    }
}

/// GET /public/tracking/{token} (ADR-0047).
internal sealed class GetWebOrderTrackingQueryHandler(
    AppDbContext db,
    TrackedOrderLoader tokens,
    IStorefrontGate storefront,
    TimeProvider time) : IQueryHandler<GetWebOrderTrackingQuery, WebOrderTrackingDto>, IPublicHandler
{
    public async Task<WebOrderTrackingDto> HandleAsync(GetWebOrderTrackingQuery query, CancellationToken cancellationToken)
    {
        var (orderId, expiresAt) = await tokens.FindAsync(query.TrackingToken, cancellationToken);
        var order = await db.Set<Order>()
            .AsNoTracking()
            .Include(o => o.Items).ThenInclude(i => i.Components)
            .AsSplitQuery()
            .SingleAsync(o => o.Id == orderId, cancellationToken);
        return await WebOrderTracking.BuildAsync(db, storefront, order, expiresAt, time.GetUtcNow(), cancellationToken);
    }
}

/// POST /public/tracking/{token}/withdraw (ADR-0046 item 17): desistência enquanto aguarda aceite.
internal sealed class WithdrawWebOrderByTrackingHandler(
    AppDbContext db,
    TrackedOrderLoader tokens,
    WebOrderWithdrawal withdrawal,
    IStorefrontGate storefront,
    TimeProvider time) : ICommandHandler<WithdrawWebOrderByTrackingCommand, WebOrderTrackingDto>, IPublicHandler
{
    public async Task<WebOrderTrackingDto> HandleAsync(WithdrawWebOrderByTrackingCommand command, CancellationToken cancellationToken)
    {
        var (orderId, expiresAt) = await tokens.FindAsync(command.TrackingToken, cancellationToken);
        var order = await withdrawal.WithdrawAsync(orderId, cancellationToken);
        return await WebOrderTracking.BuildAsync(db, storefront, order, expiresAt, time.GetUtcNow(), cancellationToken);
    }
}

/// O que o ADR-0047 (itens 5 a 7) manda mostrar, e nada além.
internal static class WebOrderTracking
{
    public static async Task<WebOrderTrackingDto> BuildAsync(
        AppDbContext db, IStorefrontGate storefront, Order order, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var store = await storefront.GetAsync(cancellationToken);
        var changedByStore = await db.Set<OrderChangeEvent>().AnyAsync(e => e.OrderId == order.Id, cancellationToken);
        var status = Status(order, now);

        var active = order.Items.Where(i => i.Status == LineStatus.Active).ToList();
        var items = active.Where(i => i.ParentOrderItemId is null)
            .OrderBy(i => i.CreatedAt).ThenBy(i => i.Id)
            .Select(i => new TrackingItemDto(
                i.ItemKind, i.NameSnapshot, i.CompositionSnapshot, i.Quantity, i.UnitPrice, i.LineTotal, i.Notes,
                active.Where(a => a.ParentOrderItemId == i.Id).OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
                    .Select(a => new TrackingAddonDto(a.NameSnapshot, a.Quantity, a.UnitPrice, a.LineTotal))
                    .ToList()))
            .ToList();

        var rejection = order.RejectionReasonCode is { } reason
            ? new TrackingRejectionDto(reason, reason == RejectionReason.SuspectedPrank ? null : order.RejectionMessage)
            : null;
        var address = store.Address is { } a ? new TrackingStoreAddressDto(a.Street, a.Number, a.Complement, a.Neighborhood, a.City, a.State) : null;

        var etag = $"\"{order.Version}-{UpperSnakeEnumConverter<TrackingStatus>.ToText(status)}-{store.SettingsVersion}-{store.Slug}\"";
        return new WebOrderTrackingDto(
            order.Version,
            new TrackingStoreDto(store.Slug, store.PublicName, store.ContactPhone, address),
            order.OrderNumber,
            order.CreatedAt,
            status,
            order.AcceptanceStatus,
            order.AcceptanceDeadline,
            order.EstimatedReadyMinutes,
            status == TrackingStatus.AwaitingAcceptance,
            Timeline(order),
            rejection,
            order.FulfillmentType,
            FirstName(order.CustomerName),
            order.FulfillmentType == FulfillmentType.Delivery && order.DeliveryAddress is { } d ? new TrackingAddressDto(d.Street, d.Number) : null,
            items,
            order.ItemsTotal,
            order.DeliveryFee,
            order.DeliveryDistanceKm,
            order.TotalAmount,
            order.ExpectedPaymentMethod,
            order.ChangeFor,
            changedByStore,
            expiresAt)
        {
            ETag = etag,
        };
    }

    /// Tabela do ADR-0047 item 7.
    public static TrackingStatus Status(Order order, DateTimeOffset now)
    {
        if (order.CancellationStatus == CancellationStatus.Canceled)
        {
            return TrackingStatus.Canceled;
        }

        return order.AcceptanceStatus switch
        {
            AcceptanceStatus.AwaitingPayment => TrackingStatus.AwaitingPayment,
            AcceptanceStatus.AwaitingAcceptance => order.HasAcceptanceExpired(now) ? TrackingStatus.Expired : TrackingStatus.AwaitingAcceptance,
            AcceptanceStatus.Rejected => TrackingStatus.Rejected,
            AcceptanceStatus.Expired => TrackingStatus.Expired,
            AcceptanceStatus.Withdrawn => TrackingStatus.Withdrawn,
            _ => (order.FulfillmentStatus, order.ProductionStatus) switch
            {
                (FulfillmentStatus.Delivered, _) => TrackingStatus.Delivered,
                (FulfillmentStatus.PickedUp, _) => TrackingStatus.PickedUp,
                (FulfillmentStatus.OutForDelivery, _) => TrackingStatus.OutForDelivery,
                (_, ProductionStatus.Completed) => order.FulfillmentType == FulfillmentType.Delivery
                    ? TrackingStatus.ReadyForDelivery
                    : TrackingStatus.ReadyForPickup,
                (_, ProductionStatus.InProgress) => TrackingStatus.InPreparation,
                _ => TrackingStatus.Confirmed,
            },
        };
    }

    private static List<TrackingStepDto> Timeline(Order o)
    {
        var steps = new List<TrackingStepDto> { new(TrackingStep.Submitted, o.CreatedAt) };
        void Add(TrackingStep step, DateTimeOffset? at)
        {
            if (at is { } value)
            {
                steps.Add(new TrackingStepDto(step, value));
            }
        }

        Add(TrackingStep.Accepted, o.IsAccepted ? o.AcceptedAt : null);
        Add(TrackingStep.InPreparation, o.ProductionStatus == ProductionStatus.InProgress ? o.ProductionStatusChangedAt : null);
        Add(TrackingStep.Ready, o.ProductionStatus == ProductionStatus.Completed ? o.ProductionStatusChangedAt : null);
        Add(TrackingStep.OutForDelivery, o.OutForDeliveryAt);
        Add(TrackingStep.Delivered, o.DeliveredAt);
        Add(TrackingStep.PickedUp, o.PickedUpAt);
        Add(TrackingStep.Rejected, o.RejectedAt);
        Add(TrackingStep.Expired, o.ExpiredAt);
        Add(TrackingStep.Withdrawn, o.WithdrawnAt);
        Add(TrackingStep.Canceled, o.CanceledAt);
        return steps.OrderBy(s => s.At).ToList();
    }

    /// Só o primeiro nome ("Pedido de Carla", ADR-0047 item 5).
    private static string FirstName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
}

/// Anonimização de um pedido da página que não virou venda (ADR-0052 item 3), no contexto do tenant, pelo job de retenção.
internal sealed record AnonymizeWebOrderCommand(Guid OrderId);

internal sealed class AnonymizeWebOrderHandler(AppDbContext db, IAuditWriter audit, TimeProvider time) : ICommandHandler<AnonymizeWebOrderCommand, bool>
{
    public async Task<bool> HandleAsync(AnonymizeWebOrderCommand command, CancellationToken cancellationToken)
    {
        var order = await OrderRules.LoadAsync(db, command.OrderId, cancellationToken);
        if (!order.AnonymizeNotAccepted(time.GetUtcNow()))
        {
            return false;
        }

        audit.Add("WebOrderAnonymized", order.SyncEntityType, order.Id, order.Id,
            after: new { acceptanceStatus = UpperSnakeEnumConverter<AcceptanceStatus>.ToText(order.AcceptanceStatus) },
            origin: AuditOrigins.System);
        return true;
    }
}
