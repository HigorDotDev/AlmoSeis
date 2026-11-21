using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Security;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Catalog.Contracts;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.Modules.Menu.Contracts;
using AlmoSeis.Modules.Storefront.Contracts;
using AlmoSeis.Modules.Storefront.Domain;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Errors;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Modules.Storefront.Application;

/// Slug → tenant (ADR-0045 item 4). Roda antes de o tenant ser conhecido: `store_slugs` tem leitura aberta ao papel da
/// aplicação (05 §5.17, 0004). Por SQL direto, sem o filtro de tenant do EF (que esconderia tudo sem tenant), numa tabela
/// sem dado pessoal.
internal sealed class StoreSlugResolver(AppDbContext db) : IStoreSlugResolver
{
    public async Task<StoreSlugResolution?> ResolveAsync(string slug, CancellationToken cancellationToken)
    {
        var normalized = slug.Trim().ToLowerInvariant();
        if (normalized.Length is < SlugRules.MinLength or > SlugRules.MaxLength)
        {
            return null;
        }

        var rows = await db.Database.SqlQuery<SlugRow>(
            $"""
            SELECT s.tenant_id, c.slug AS current_slug
              FROM store_slugs s
              JOIN store_slugs c ON c.tenant_id = s.tenant_id AND c.is_current
             WHERE s.slug = {normalized}
            """).ToListAsync(cancellationToken);
        return rows is [var row] ? new StoreSlugResolution(row.TenantId, normalized, row.CurrentSlug) : null;
    }

    private sealed record SlugRow(Guid TenantId, string CurrentSlug);
}

/// O que o envio e o acompanhamento precisam saber da página, no tenant resolvido pelo slug ou pelo token.
internal sealed class StorefrontGate(
    AppDbContext db,
    ITenantContext tenant,
    StorefrontStatusCalculator status,
    PublicMenuBuilder menus,
    IMapsAvailability maps) : IStorefrontGate
{
    public async Task<StorefrontOrderingContext> GetAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId!.Value;
        var settings = await db.Set<StorefrontSettings>().AsNoTracking().SingleAsync(s => s.Id == tenantId, cancellationToken);
        var (storeStatus, date) = await status.ComputeWithDateAsync(settings, cancellationToken);
        var slug = await UpdateStorefrontHandler.CurrentSlugAsync(db, tenantId, cancellationToken);
        return new StorefrontOrderingContext(
            storeStatus, slug, settings.PublicName, settings.ContactPhone, settings.PublicAddress,
            settings.OffersPickup, settings.IsDeliveryAvailableOnPage(maps.IsConfigured), date, settings.Version);
    }

    public Task<PublicMenuDto?> GetMenuAsync(DateOnly businessDate, CancellationToken cancellationToken) =>
        menus.BuildAsync(businessDate, cancellationToken);
}

/// Cardápio da página (ADR-0044 item 5, RF-202): o `DAILY` de `MARMITA` da data do expediente aberto, tamanhos ativos,
/// adicionais e bebidas ativos e visíveis na página. Indisponíveis aparecem desabilitados. Sem cardápio do dia → nulo.
internal sealed class PublicMenuBuilder(IMenuReader menus, ICatalogReader catalog)
{
    public async Task<PublicMenuDto?> BuildAsync(DateOnly businessDate, CancellationToken cancellationToken)
    {
        var daily = await menus.GetDailyAsync(MenuType.Marmita, businessDate, cancellationToken);
        if (daily is null)
        {
            return null;
        }

        var dishes = await catalog.GetProductsAsync(daily.Items.Select(i => i.ProductId).ToList(), cancellationToken);
        var sizes = await catalog.ListActiveMarmitaSizesAsync(cancellationToken);
        var extras = await catalog.ListActiveAsync([ProductKind.Addon, ProductKind.Beverage], cancellationToken);

        List<PublicDishDto> Section(MenuSection section) => daily.Items
            .Where(i => i.Section == section && dishes.TryGetValue(i.ProductId, out var p) && p.IsActive)
            .Select(i => (Item: i, Product: dishes[i.ProductId]))
            .Select(x => new PublicDishDto(x.Product.Id, x.Product.Name, x.Product.Description, x.Product.ImageUrl, x.Item.IsAvailable && x.Product.IsAvailable))
            .ToList();

        List<PublicPricedProductDto> Priced(ProductKind kind) => extras
            .Where(p => p.Kind == kind && p.IsVisibleOnPage && p.DefaultPrice is not null)
            .Select(p => new PublicPricedProductDto(p.Id, p.Name, p.Description, p.ImageUrl, p.DefaultPrice!.Value, p.IsAvailable))
            .ToList();

        return new PublicMenuDto(
            sizes.Select(s => new PublicMarmitaSizeDto(s.Id, s.Name, s.Price)).ToList(),
            Section(MenuSection.MainDish),
            Section(MenuSection.SideDish),
            Priced(ProductKind.Addon),
            Priced(ProductKind.Beverage));
    }
}

/// GET /public/stores/{slug} (06 §6.19). Cardápio só com o expediente aberto e a página disponível (OPEN ou PAUSED).
internal sealed class GetPublicStoreQueryHandler(
    AppDbContext db,
    ITenantContext tenant,
    StorefrontStatusCalculator status,
    PublicMenuBuilder menus,
    IMapsAvailability maps,
    ITenantSettingsReader tenantSettings,
    IOptions<TurnstileOptions> turnstile) : IQueryHandler<GetPublicStoreQuery, PublicStoreDto>, IPublicHandler
{
    public const string OperatorName = "AlmoSeis";

    public async Task<PublicStoreDto> HandleAsync(GetPublicStoreQuery query, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId!.Value;
        var settings = await db.Set<StorefrontSettings>().AsNoTracking().SingleAsync(s => s.Id == tenantId, cancellationToken);
        var slug = await UpdateStorefrontHandler.CurrentSlugAsync(db, tenantId, cancellationToken);
        var (storeStatus, date) = await status.ComputeWithDateAsync(settings, cancellationToken);

        if (storeStatus == StoreStatus.Unavailable)
        {
            // "Página indisponível", sem dizer o motivo (ADR-0044 item 6, ADR-0045 item 16).
            return new PublicStoreDto(slug, storeStatus, settings.PublicName, null, null, null, false, false, null, null, null, null, PublicOrderLimits.Dto);
        }

        var menu = date is { } businessDate ? await menus.BuildAsync(businessDate, cancellationToken) : null;
        var delivery = settings.IsDeliveryAvailableOnPage(maps.IsConfigured);
        return new PublicStoreDto(
            slug, storeStatus, settings.PublicName, settings.PublicAddress, settings.ContactPhone, settings.HoursText,
            settings.OffersPickup, delivery, delivery ? await DeliveryRuleAsync(settings, cancellationToken) : null,
            new PublicPrivacyDto(settings.PublicName, settings.ContactPhone, OperatorName, ThirdParties(settings, delivery)),
            date, menu, PublicOrderLimits.Dto);
    }

    /// A regra da taxa só aparece com a entrega pela página disponível.
    private async Task<PublicDeliveryRuleDto> DeliveryRuleAsync(StorefrontSettings s, CancellationToken cancellationToken) =>
        s.DeliveryFeeMode == DeliveryFeeMode.Fixed
            ? new PublicDeliveryRuleDto(DeliveryFeeMode.Fixed, null, null, null, (await tenantSettings.GetAsync(cancellationToken)).DefaultDeliveryFee)
            : new PublicDeliveryRuleDto(DeliveryFeeMode.Distance, s.PricePerKm, s.MinimumDeliveryFee, StorefrontDtos.FormatKm(s.MaxDeliveryDistanceKm), null);

    /// `MAPS` quando o endereço do cliente vai a um provedor externo (ADR-0052 itens 1 e 8).
    private IReadOnlyList<string> ThirdParties(StorefrontSettings s, bool delivery)
    {
        var parties = new List<string>();
        if (turnstile.Value.Enabled)
        {
            parties.Add("ANTI_BOT");
        }

        if (delivery && s.DeliveryFeeMode == DeliveryFeeMode.Distance && maps.IsExternal)
        {
            parties.Add("MAPS");
        }

        return parties;
    }
}

/// POST /public/stores/{slug}/delivery-quotes (06 §6.19, ADR-0048 item 8). Ordem: página `OPEN` (`StoreUnavailable`) →
/// Turnstile (`CaptchaFailed`; fora do ar segue, ADR-0045 item 12) → entrega disponível (`DeliveryUnavailable`) →
/// endereço encontrado e exato (`422` em `address.*`) → rota (`OutOfDeliveryArea` sem rota ou fora do raio) → grava a
/// cotação por 15 min. Provedor fora do ar → `DeliveryUnavailable`, e a página oferece retirada.
internal sealed class DeliveryQuoteHandler(
    AppDbContext db,
    ITenantContext tenant,
    StorefrontStatusCalculator status,
    ITurnstileVerifier turnstile,
    DeliveryQuoteCalculator calculator)
    : ICommandHandler<DeliveryQuoteCommand, DeliveryQuoteDto>, IPublicHandler
{
    public async Task<DeliveryQuoteDto> HandleAsync(DeliveryQuoteCommand command, CancellationToken cancellationToken)
    {
        var settings = await db.Set<StorefrontSettings>().AsNoTracking().SingleAsync(s => s.Id == tenant.TenantId, cancellationToken);
        var storeStatus = await status.ComputeAsync(settings, cancellationToken);
        if (storeStatus != StoreStatus.Open)
        {
            throw StoreErrors.Unavailable(storeStatus);
        }

        if (await turnstile.VerifyAsync(command.TurnstileToken, command.ClientIp, cancellationToken) == TurnstileOutcome.Failed)
        {
            throw new BusinessException(ErrorCodes.CaptchaFailed);
        }

        var quote = await calculator.QuoteAsync(settings, command.Request.Address, "address", cancellationToken);
        db.Add(quote);
        return quote.ToDto();
    }
}

/// Endereço da cotação (06 §6.19): os mesmos limites do endereço do envio.
internal sealed class DeliveryQuoteRequestValidator : AbstractValidator<DeliveryQuoteRequest>
{
    public DeliveryQuoteRequestValidator()
    {
        RuleFor(x => x.Address).NotNull().WithMessage("Informe o endereço de entrega.");
        RuleFor(x => x.Address.Street).NotEmpty().WithMessage("Informe a rua.").MaximumLength(120).When(x => x.Address is not null);
        RuleFor(x => x.Address.Number).MaximumLength(10).When(x => x.Address is not null);
        RuleFor(x => x.Address.Complement).MaximumLength(60).When(x => x.Address is not null);
        RuleFor(x => x.Address.Neighborhood).MaximumLength(60).When(x => x.Address is not null);
        RuleFor(x => x.Address.City).MaximumLength(80).When(x => x.Address is not null);
        RuleFor(x => x.Address.ZipCode).Matches(@"^[0-9]{5}-?[0-9]{3}$").WithMessage("CEP com 8 dígitos.").When(x => x.Address is not null);
        RuleFor(x => x.Address.Reference).MaximumLength(120).When(x => x.Address is not null);
    }
}
