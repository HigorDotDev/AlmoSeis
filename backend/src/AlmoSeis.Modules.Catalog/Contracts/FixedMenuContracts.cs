using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;

namespace AlmoSeis.Modules.Catalog.Contracts;

/// Tabela de preço usada por um pedido (ADR-0057 item 7): `COUNTER` (salão, retirada e entrega do app) ou `SITE`
/// (pedido da página). Gravada em `orders.price_list`.
public enum PriceList
{
    Counter,
    Site,
}

/// Componente de combo (ADR-0056 item 13): prato com tamanho fixo ou tamanho de bebida (sabor escolhido no pedido).
public enum ComboComponentKind
{
    Dish,
    Beverage,
}

// ---------- DTOs (06 §6.5 "Cardápio fixo") ----------

/// `GET /fixed-menu`: o cardápio fixo inteiro numa chamada, com a disponibilidade derivada (`isSellable`).
public sealed record FixedMenuDto(
    IReadOnlyList<DishCategoryDto> Categories,
    IReadOnlyList<DishDto> Dishes,
    IReadOnlyList<BeverageTypeDto> BeverageTypes,
    IReadOnlyList<ComboDto> Combos,
    IReadOnlyList<AddonGroupDto> AddonGroups);

public sealed record DishCategoryDto(
    Guid Id, int Version, string Name, int SortOrder, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt) : IVersionedDto;

public sealed record DishDto(
    Guid Id,
    int Version,
    Guid DishCategoryId,
    string Name,
    string? Description,
    string? ImageUrl,
    bool RequiresPreparation,
    bool HasSizes,
    Money? Price,
    bool SellsOnSite,
    Money? SitePrice,
    int SortOrder,
    bool IsActive,
    bool IsPaused,
    bool IsSellable,
    IReadOnlyList<DishSizeDto> Sizes,
    IReadOnlyList<Guid> AddonGroupIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt) : IVersionedDto;

public sealed record DishSizeDto(
    Guid Id, string Name, Money Price, bool SellsOnSite, Money? SitePrice, int SortOrder, bool IsActive, bool IsPaused, bool IsSellable);

public sealed record BeverageTypeDto(
    Guid Id,
    int Version,
    string Name,
    int SortOrder,
    bool IsActive,
    IReadOnlyList<BeverageSizeDto> Sizes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt) : IVersionedDto;

public sealed record BeverageSizeDto(
    Guid Id,
    string Name,
    Money Price,
    bool SellsOnSite,
    Money? SitePrice,
    int SortOrder,
    bool IsActive,
    bool IsPaused,
    bool IsSellable,
    IReadOnlyList<BeverageFlavorDto> Flavors);

public sealed record BeverageFlavorDto(Guid Id, string Name, Money ExtraPrice, int SortOrder, bool IsActive, bool IsPaused);

public sealed record ComboDto(
    Guid Id,
    int Version,
    string Name,
    string? Description,
    string? ImageUrl,
    Money Price,
    bool SellsOnSite,
    Money? SitePrice,
    int SortOrder,
    bool IsActive,
    bool IsPaused,
    bool IsSellable,
    IReadOnlyList<ComboComponentDto> Components,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt) : IVersionedDto;

/// `name`: "X-Burguer", "Batata frita P", "Refrigerante Lata 350 ml". `isSellable: false` = o motivo do combo fora de venda.
public sealed record ComboComponentDto(
    Guid Id, ComboComponentKind Kind, Guid? DishId, Guid? DishSizeId, Guid? BeverageSizeId, string Name, int Quantity, int SortOrder, bool IsSellable);

public sealed record AddonGroupDto(
    Guid Id,
    int Version,
    string Name,
    int SortOrder,
    bool IsActive,
    IReadOnlyList<AddonOptionDto> Options,
    IReadOnlyList<Guid> DishCategoryIds,
    IReadOnlyList<Guid> DishIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt) : IVersionedDto;

public sealed record AddonOptionDto(Guid Id, string Name, Money Price, int SortOrder, bool IsActive, bool IsPaused);

// ---------- Consultas ----------

public sealed record GetFixedMenuQuery(bool IncludeInactive);

public sealed record GetDishCategoryQuery(Guid Id);

public sealed record GetDishQuery(Guid Id);

public sealed record GetBeverageTypeQuery(Guid Id);

public sealed record GetComboQuery(Guid Id);

public sealed record GetAddonGroupQuery(Guid Id);

// ---------- Cadastro (`catalog.manage`) ----------

public sealed record CreateDishCategoryCommand(string Name, int SortOrder);

public sealed record UpdateDishCategoryRequest(Optional<string> Name, Optional<int> SortOrder, Optional<bool> IsActive);

public sealed record UpdateDishCategoryCommand(Guid DishCategoryId, UpdateDishCategoryRequest Changes);

/// Tamanho no corpo: `id` nulo = novo; com `id` = o tamanho existente. Na edição, a lista substitui a do prato e os
/// tamanhos fora dela são inativados.
public sealed record DishSizeInput(Guid? Id, string Name, Money Price, bool SellsOnSite, Money? SitePrice, int SortOrder);

public sealed record CreateDishCommand(
    Guid DishCategoryId,
    string Name,
    string? Description,
    bool? RequiresPreparation,
    bool HasSizes,
    Money? Price,
    bool SellsOnSite,
    Money? SitePrice,
    int SortOrder,
    IReadOnlyList<DishSizeInput>? Sizes);

public sealed record UpdateDishRequest(
    Optional<Guid> DishCategoryId,
    Optional<string> Name,
    Optional<string?> Description,
    Optional<bool> RequiresPreparation,
    Optional<bool> HasSizes,
    Optional<Money?> Price,
    Optional<bool> SellsOnSite,
    Optional<Money?> SitePrice,
    Optional<int> SortOrder,
    Optional<bool> IsActive,
    Optional<string?> ImageStorageKey,
    Optional<IReadOnlyList<DishSizeInput>> Sizes);

public sealed record UpdateDishCommand(Guid DishId, UpdateDishRequest Changes);

public sealed record BeverageFlavorInput(Guid? Id, string Name, Money ExtraPrice, int SortOrder);

public sealed record BeverageSizeInput(
    Guid? Id, string Name, Money Price, bool SellsOnSite, Money? SitePrice, int SortOrder, IReadOnlyList<BeverageFlavorInput>? Flavors);

public sealed record CreateBeverageTypeCommand(string Name, int SortOrder, IReadOnlyList<BeverageSizeInput>? Sizes);

public sealed record UpdateBeverageTypeRequest(
    Optional<string> Name, Optional<int> SortOrder, Optional<bool> IsActive, Optional<IReadOnlyList<BeverageSizeInput>> Sizes);

public sealed record UpdateBeverageTypeCommand(Guid BeverageTypeId, UpdateBeverageTypeRequest Changes);

public sealed record ComboComponentInput(ComboComponentKind Kind, Guid? DishId, Guid? DishSizeId, Guid? BeverageSizeId, int Quantity);

public sealed record CreateComboCommand(
    string Name,
    string? Description,
    Money Price,
    bool SellsOnSite,
    Money? SitePrice,
    int SortOrder,
    IReadOnlyList<ComboComponentInput> Components);

public sealed record UpdateComboRequest(
    Optional<string> Name,
    Optional<string?> Description,
    Optional<Money> Price,
    Optional<bool> SellsOnSite,
    Optional<Money?> SitePrice,
    Optional<int> SortOrder,
    Optional<bool> IsActive,
    Optional<string?> ImageStorageKey,
    Optional<IReadOnlyList<ComboComponentInput>> Components);

public sealed record UpdateComboCommand(Guid ComboId, UpdateComboRequest Changes);

public sealed record AddonOptionInput(Guid? Id, string Name, Money Price, int SortOrder);

public sealed record CreateAddonGroupCommand(
    string Name,
    int SortOrder,
    IReadOnlyList<AddonOptionInput>? Options,
    IReadOnlyList<Guid>? DishCategoryIds,
    IReadOnlyList<Guid>? DishIds);

public sealed record UpdateAddonGroupRequest(
    Optional<string> Name,
    Optional<int> SortOrder,
    Optional<bool> IsActive,
    Optional<IReadOnlyList<AddonOptionInput>> Options,
    Optional<IReadOnlyList<Guid>> DishCategoryIds,
    Optional<IReadOnlyList<Guid>> DishIds);

public sealed record UpdateAddonGroupCommand(Guid AddonGroupId, UpdateAddonGroupRequest Changes);

/// `POST /fixed-menu/dishes/{id}/image-upload` e `/combos/{id}/image-upload`. `EntityType`: `DISH` ou `COMBO`.
public sealed record RequestFixedMenuImageUploadCommand(string EntityType, Guid EntityId, RequestImageUploadRequest Request);

// ---------- Pausa (`catalog.availability`) ----------

public sealed record PauseInput(Guid Id, bool IsPaused);

public sealed record SetDishAvailabilityRequest(bool? IsPaused, IReadOnlyList<PauseInput>? Sizes);

public sealed record SetDishAvailabilityCommand(Guid DishId, SetDishAvailabilityRequest Request);

public sealed record SetBeverageAvailabilityRequest(IReadOnlyList<PauseInput>? Sizes, IReadOnlyList<PauseInput>? Flavors);

public sealed record SetBeverageAvailabilityCommand(Guid BeverageTypeId, SetBeverageAvailabilityRequest Request);

public sealed record SetComboAvailabilityRequest(bool IsPaused);

public sealed record SetComboAvailabilityCommand(Guid ComboId, SetComboAvailabilityRequest Request);

public sealed record SetAddonAvailabilityRequest(IReadOnlyList<PauseInput>? Options);

public sealed record SetAddonAvailabilityCommand(Guid AddonGroupId, SetAddonAvailabilityRequest Request);

// ---------- Porta de leitura para o pedido à la carte (fatia 3, ADR-0058) ----------

/// Preço de uma unidade vendável (ADR-0057): balcão e, com "vende no site", o de site.
public sealed record FixedMenuPrice(Money Counter, bool SellsOnSite, Money? Site)
{
    /// Pedido `SITE` usa o preço de site quando a unidade vende no site; senão o de balcão (ADR-0057 itens 6 e 8, Q-309).
    public Money For(PriceList priceList) =>
        priceList == PriceList.Site && SellsOnSite && Site is { } site ? site : Counter;
}

public sealed record FixedMenuDishSize(Guid Id, string Name, FixedMenuPrice Price, bool IsSellable);

/// `AddonOptionIds`: as opções ativas dos grupos ativos da categoria do prato ∪ do próprio prato, sem repetir (item 18).
public sealed record FixedMenuDish(
    Guid Id,
    Guid DishCategoryId,
    string Name,
    bool RequiresPreparation,
    bool HasSizes,
    FixedMenuPrice? Price,
    bool IsSellable,
    IReadOnlyDictionary<Guid, FixedMenuDishSize> Sizes,
    IReadOnlyList<Guid> AddonOptionIds);

public sealed record FixedMenuBeverageFlavor(Guid Id, string Name, Money ExtraPrice, bool IsSellable);

/// `Name`: "Lata 350 ml"; `TypeName`: "Refrigerante". `Flavors`: só os ativos. Com sabores, o sabor é obrigatório (item 11).
public sealed record FixedMenuBeverageSize(
    Guid Id,
    Guid BeverageTypeId,
    string TypeName,
    string Name,
    FixedMenuPrice Price,
    bool IsSellable,
    IReadOnlyDictionary<Guid, FixedMenuBeverageFlavor> Flavors);

public sealed record FixedMenuComboComponent(
    ComboComponentKind Kind, Guid? DishId, Guid? DishSizeId, Guid? BeverageSizeId, int Quantity, string Name, bool IsSellable);

public sealed record FixedMenuCombo(Guid Id, string Name, FixedMenuPrice Price, bool IsSellable, IReadOnlyList<FixedMenuComboComponent> Components);

public sealed record FixedMenuAddonOption(Guid Id, Guid AddonGroupId, string Name, Money Price, bool IsSellable);

/// O cardápio fixo do tenant corrente, só com itens ativos, com a disponibilidade derivada (ADR-0056 item 23).
public sealed record FixedMenuSnapshot(
    IReadOnlyDictionary<Guid, FixedMenuDish> Dishes,
    IReadOnlyDictionary<Guid, FixedMenuBeverageSize> BeverageSizes,
    IReadOnlyDictionary<Guid, FixedMenuCombo> Combos,
    IReadOnlyDictionary<Guid, FixedMenuAddonOption> AddonOptions);

/// Leitura do cardápio fixo por outros módulos (Ordering na fatia 3, Storefront na fatia 5), no tenant corrente.
/// Sem o módulo `FIXED_MENU`, o snapshot é vazio.
public interface IFixedMenuReader
{
    Task<FixedMenuSnapshot> LoadAsync(CancellationToken cancellationToken);
}
