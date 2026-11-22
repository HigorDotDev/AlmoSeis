using AlmoSeis.Modules.Catalog.Contracts;

namespace AlmoSeis.Modules.Catalog.Domain;

/// Disponibilidade derivada do cardápio fixo (ADR-0056 item 23; 04 §4.5.1). Calculada a cada leitura, **nunca gravada**:
/// pausar um prato, um tamanho ou um sabor tira de venda os combos que o usam sem tocar no combo, e reativar devolve.
/// É a única implementação da regra: o `GET /fixed-menu`, os DTOs das escritas e o `IFixedMenuReader` usam esta classe.
internal sealed class FixedMenuAvailability
{
    private readonly IReadOnlyDictionary<Guid, Dish> _dishes;
    private readonly IReadOnlyDictionary<Guid, (BeverageType Type, BeverageSize Size)> _beverageSizes;

    public FixedMenuAvailability(IEnumerable<Dish> dishes, IEnumerable<BeverageType> beverageTypes)
    {
        _dishes = dishes.ToDictionary(d => d.Id);
        _beverageSizes = beverageTypes.SelectMany(t => t.Sizes.Select(s => (Type: t, Size: s))).ToDictionary(x => x.Size.Id);
    }

    /// Prato vendável = ativo, não pausado e, com tamanho, com pelo menos um tamanho ativo e não pausado.
    public static bool IsSellable(Dish dish) =>
        dish.IsActive && !dish.IsPaused && (!dish.HasSizes || dish.Sizes.Any(s => IsSellable(dish, s)));

    public static bool IsSellable(Dish dish, DishSize size) =>
        dish.IsActive && !dish.IsPaused && dish.HasSizes && size.IsActive && !size.IsPaused;

    /// Tamanho de bebida vendável = tipo e tamanho ativos, tamanho não pausado e, com sabores ativos, pelo menos um não
    /// pausado. Sem sabor ativo, é vendido sem escolha (item 11).
    public static bool IsSellable(BeverageType type, BeverageSize size)
    {
        var flavors = size.Flavors.Where(f => f.IsActive).ToList();
        return type.IsActive && size.IsActive && !size.IsPaused && (flavors.Count == 0 || flavors.Any(f => !f.IsPaused));
    }

    public static bool IsSellable(BeverageFlavor flavor) => flavor.IsActive && !flavor.IsPaused;

    public static bool IsSellable(AddonGroup group, AddonOption option) => group.IsActive && option.IsActive && !option.IsPaused;

    /// Combo vendável = ativo, não pausado e **todos** os componentes vendáveis.
    public bool IsSellable(Combo combo) =>
        combo.IsActive && !combo.IsPaused && combo.Components.All(c => Component(c).IsSellable);

    /// Nome do componente para o app ("Pausado: Batata frita P") e se ele está vendável agora.
    public (string Name, bool IsSellable) Component(ComboComponent component)
    {
        if (component.Kind == ComboComponentKind.Beverage)
        {
            return component.BeverageSizeId is { } sizeId && _beverageSizes.TryGetValue(sizeId, out var beverage)
                ? ($"{beverage.Type.Name} {beverage.Size.Name}", IsSellable(beverage.Type, beverage.Size))
                : ("Bebida", false);
        }

        if (component.DishId is not { } dishId || !_dishes.TryGetValue(dishId, out var dish))
        {
            return ("Prato", false);
        }

        if (component.DishSizeId is not { } dishSizeId)
        {
            return (dish.Name, !dish.HasSizes && IsSellable(dish));
        }

        var size = dish.Sizes.SingleOrDefault(s => s.Id == dishSizeId);
        return size is null ? (dish.Name, false) : ($"{dish.Name} {size.Name}", IsSellable(dish, size));
    }
}
