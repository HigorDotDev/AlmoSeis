using System.Globalization;
using AlmoSeis.Infrastructure.Audit;
using AlmoSeis.Infrastructure.Concurrency;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Billing.Contracts;
using AlmoSeis.Modules.Catalog.Contracts;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.Modules.Menu.Contracts;
using AlmoSeis.Modules.Storefront.Contracts;
using AlmoSeis.Modules.Storefront.Domain;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;

namespace AlmoSeis.Modules.Storefront.Application;

/// Autocadastro (ADR-0044 itens 11 e 12): a página nasce inativa e o slug do nome fica reservado, na transação do cadastro.
internal sealed class StorefrontProvisioner(AppDbContext db, SlugWriter slugs) : ITenantCreatedListener
{
    public async Task OnTenantCreatedAsync(NewTenantInfo tenant, CancellationToken cancellationToken)
    {
        db.Add(StorefrontSettings.CreateDefault(tenant.TenantId, tenant.Name, tenant.CreatedAt));
        await slugs.ReserveFirstFreeAsync(tenant.TenantId, SlugRules.Candidates(tenant.Name, tenant.City), tenant.CreatedAt, cancellationToken);
    }
}

/// Gravação em `store_slugs` por SQL: a unicidade é global e a RLS esconde nada na leitura, mas uma violação de PK
/// abortaria a transação; `ON CONFLICT DO NOTHING` permite tentar o próximo candidato (como o código de convite).
internal sealed class SlugWriter(AppDbContext db)
{
    public async Task<string> ReserveFirstFreeAsync(Guid tenantId, IEnumerable<string> candidates, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates)
        {
            var inserted = await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO store_slugs (slug, tenant_id, is_current, created_at)
                VALUES ({candidate}, {tenantId}, true, {now})
                ON CONFLICT (slug) DO NOTHING
                """,
                cancellationToken);
            if (inserted == 1)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Nenhum slug livre para o estabelecimento.");
    }

    /// Troca o slug atual (ADR-0044 item 15). O antigo continua do tenant e redireciona. Voltar a um slug antigo do próprio
    /// tenant o reativa. Outro tenant (atual, antigo ou em quarentena) → `422` em `slug`.
    public async Task ChangeAsync(Guid tenantId, string current, string next, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var owner = await db.Set<StoreSlug>().AsNoTracking().Where(s => s.Slug == next).Select(s => (Guid?)s.TenantId).SingleOrDefaultAsync(cancellationToken);
        if (owner is not null && owner != tenantId)
        {
            throw BusinessException.Validation("slug", "Este endereço já está em uso. Escolha outro.");
        }

        if (owner is null)
        {
            var previous = await db.Set<StoreSlug>().CountAsync(s => s.TenantId == tenantId && !s.IsCurrent, cancellationToken);
            if (previous >= SlugRules.MaxPrevious)
            {
                throw BusinessException.Validation(
                    "slug", $"O endereço da página já foi trocado {SlugRules.MaxPrevious} vezes. Fale com o suporte para trocar de novo.");
            }
        }

        // O atual sai antes de o próximo entrar: ux_store_slugs_current é por comando.
        await db.Database.ExecuteSqlAsync(
            $"UPDATE store_slugs SET is_current = false, replaced_at = {now} WHERE tenant_id = {tenantId} AND slug = {current}",
            cancellationToken);

        if (owner == tenantId)
        {
            await db.Database.ExecuteSqlAsync(
                $"UPDATE store_slugs SET is_current = true, replaced_at = NULL WHERE tenant_id = {tenantId} AND slug = {next}",
                cancellationToken);
            return;
        }

        var inserted = await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO store_slugs (slug, tenant_id, is_current, created_at)
            VALUES ({next}, {tenantId}, true, {now})
            ON CONFLICT (slug) DO NOTHING
            """,
            cancellationToken);
        if (inserted == 0)
        {
            throw BusinessException.Validation("slug", "Este endereço já está em uso. Escolha outro.");
        }
    }
}

/// GET /storefront (06 §6.18).
internal sealed class GetStorefrontQueryHandler(AppDbContext db, ITenantContext tenant, StorefrontStatusCalculator status)
    : IQueryHandler<GetStorefrontQuery, StorefrontDto>
{
    public async Task<StorefrontDto> HandleAsync(GetStorefrontQuery query, CancellationToken cancellationToken)
    {
        var settings = await db.Set<StorefrontSettings>().AsNoTracking().SingleAsync(s => s.Id == tenant.TenantId, cancellationToken);
        return await StorefrontDtos.BuildAsync(db, settings, await status.ComputeAsync(settings, cancellationToken), cancellationToken);
    }
}

/// PUT /storefront [M] (06 §6.18). Permissão `storefront.manage` (ADR-0046 item 22). Auditoria sem telefone nem endereço.
internal sealed class UpdateStorefrontHandler(
    AppDbContext db,
    ITenantContext tenant,
    IConcurrencyContext concurrency,
    ICatalogReader catalog,
    SlugWriter slugs,
    StorefrontStatusCalculator status,
    IAuditWriter audit,
    TimeProvider time) : ICommandHandler<UpdateStorefrontCommand, StorefrontDto>
{
    public async Task<StorefrontDto> HandleAsync(UpdateStorefrontCommand command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId!.Value;
        var settings = await db.Set<StorefrontSettings>().SingleAsync(s => s.Id == tenantId, cancellationToken);
        settings.EnsureVersion(concurrency.ExpectedVersion);
        var request = command.Request;
        var now = time.GetUtcNow();

        var slug = request.Slug.Trim().ToLowerInvariant();
        if (SlugRules.Problem(slug) is { } problem)
        {
            throw BusinessException.Validation("slug", problem);
        }

        var current = await CurrentSlugAsync(db, tenantId, cancellationToken);
        var before = Audit(settings, current);
        var slugChanged = slug != current;
        if (slugChanged)
        {
            await slugs.ChangeAsync(tenantId, current, slug, now, cancellationToken);
        }

        var hasActiveSize = !request.IsPageActive || (await catalog.ListActiveMarmitaSizesAsync(cancellationToken)).Count > 0;
        var delivery = request.Delivery;
        var changed = settings.Update(
            new StorefrontChanges(
                request.PublicName, request.IsPageActive, request.IsAcceptingOrders, request.IsDeliveryEnabled, request.OffersDelivery,
                request.OffersPickup, request.ContactPhone, request.HoursText, request.Address,
                new DeliveryRule(delivery.FeeMode, delivery.PricePerKm, delivery.MinimumFee, StorefrontDtos.ParseKm(delivery.MaxDistanceKm))),
            slugChanged,
            hasActiveSize,
            now);

        if (changed)
        {
            audit.Add("StorefrontUpdated", "STOREFRONT", tenantId, before: before, after: Audit(settings, slug));
        }

        return await StorefrontDtos.BuildAsync(db, settings, await status.ComputeAsync(settings, cancellationToken), cancellationToken, slug);
    }

    public static async Task<string> CurrentSlugAsync(AppDbContext db, Guid tenantId, CancellationToken cancellationToken) =>
        await db.Set<StoreSlug>().AsNoTracking().Where(s => s.TenantId == tenantId && s.IsCurrent).Select(s => s.Slug).SingleAsync(cancellationToken);

    /// Sem telefone de contato nem endereço (ADR-0024: auditoria sem dados de contato).
    private static object Audit(StorefrontSettings s, string slug) => new
    {
        slug,
        s.PublicName,
        s.IsPageActive,
        s.IsAcceptingOrders,
        s.IsDeliveryEnabled,
        s.OffersDelivery,
        s.OffersPickup,
        deliveryFeeMode = UpperSnakeEnumConverter<DeliveryFeeMode>.ToText(s.DeliveryFeeMode),
        pricePerKm = s.PricePerKm?.ToString(),
        minimumDeliveryFee = s.MinimumDeliveryFee.ToString(),
        s.MaxDeliveryDistanceKm,
    };
}

internal sealed class StorefrontCurrentReader(IQueryHandler<GetStorefrontQuery, StorefrontDto> query) : ICurrentResourceReader
{
    /// Nome que o `AggregateRoot.EnsureVersion` usa para um agregado fora do feed.
    public string EntityType => "STOREFRONTSETTINGS";

    public async Task<IVersionedDto?> ReadAsync(Guid entityId, CancellationToken cancellationToken) =>
        await query.HandleAsync(new GetStorefrontQuery(), cancellationToken);
}

/// Estado da página (ADR-0044 item 6), na ordem: UNAVAILABLE (página inativa ou assinatura sem escrita) → CLOSED (sem
/// expediente aberto) → PAUSED → OPEN.
internal sealed class StorefrontStatusCalculator(ITenantContext tenant, ISubscriptionAccess subscriptions, IOpenBusinessDate openDay)
{
    public async Task<(StoreStatus Status, DateOnly? BusinessDate)> ComputeWithDateAsync(StorefrontSettings settings, CancellationToken cancellationToken)
    {
        if (!settings.IsPageActive || !(await subscriptions.GetAsync(tenant.TenantId!.Value, cancellationToken)).CanWrite)
        {
            return (StoreStatus.Unavailable, null);
        }

        var date = await openDay.GetAsync(cancellationToken);
        if (date is null)
        {
            return (StoreStatus.Closed, null);
        }

        return (settings.IsAcceptingOrders ? StoreStatus.Open : StoreStatus.Paused, date);
    }

    public async Task<StoreStatus> ComputeAsync(StorefrontSettings settings, CancellationToken cancellationToken) =>
        (await ComputeWithDateAsync(settings, cancellationToken)).Status;
}

internal static class StorefrontDtos
{
    public static async Task<StorefrontDto> BuildAsync(
        AppDbContext db, StorefrontSettings s, StoreStatus status, CancellationToken cancellationToken, string? currentSlug = null)
    {
        var slugs = await db.Set<StoreSlug>().AsNoTracking()
            .Where(x => x.TenantId == s.Id)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new { x.Slug, x.IsCurrent, x.ReplacedAt })
            .ToListAsync(cancellationToken);
        var current = currentSlug ?? slugs.Single(x => x.IsCurrent).Slug;
        var previous = slugs.Where(x => x.Slug != current).OrderByDescending(x => x.ReplacedAt).Select(x => x.Slug).ToList();

        return new StorefrontDto(
            s.Version, current, previous, s.PublicName, s.IsPageActive, s.IsAcceptingOrders, s.IsDeliveryEnabled, s.OffersDelivery, s.OffersPickup,
            s.ContactPhone, s.HoursText,
            new StorefrontAddressDto(s.AddressStreet, s.AddressNumber, s.AddressComplement, s.AddressNeighborhood, s.AddressCity, s.AddressState, s.AddressZipCode),
            s.Latitude is { } lat && s.Longitude is { } lng ? new StorefrontLocationDto(lat, lng) : null,
            new StorefrontDeliveryDto(s.DeliveryFeeMode, s.PricePerKm, s.MinimumDeliveryFee, FormatKm(s.MaxDeliveryDistanceKm)),
            status, s.UpdatedAt);
    }

    public static string? FormatKm(decimal? km) => km?.ToString("0.0", CultureInfo.InvariantCulture);

    public static decimal? ParseKm(string? km) =>
        string.IsNullOrWhiteSpace(km) ? null : decimal.Parse(km, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
}
