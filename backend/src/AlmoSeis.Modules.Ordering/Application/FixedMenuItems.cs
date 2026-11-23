using AlmoSeis.Modules.Catalog.Contracts;
using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.Modules.Ordering.Domain;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;

namespace AlmoSeis.Modules.Ordering.Application;

/// Ramo do cardápio fixo do `OrderItemResolver` (ADR-0058, sem cardápio do dia nem `MenuNotFound`): valida a
/// disponibilidade derivada do `IFixedMenuReader` (pausa → `422 ProductUnavailable`), o tamanho do prato, o sabor do
/// tamanho de bebida e os grupos de adicionais do prato (categoria ∪ prato), e monta os snapshots com o preço da tabela
/// do pedido. O combo vira `COMBO` (preço do combo) → `COMBO_PART` (um por componente, `0.00` ou o acréscimo do sabor)
/// → `GROUP_ADDON` sob as partes de prato.
///
/// `siteOnly` (carrinho da página, EP-003 fatia 5): o prato, o tamanho de prato, o tamanho de bebida e o combo pedidos
/// precisam **vender no site** (ADR-0057 item 3); senão, `ProductUnavailable`, como a pausa. Os componentes do combo não
/// (item 4). Fora da página (`false`), o pedido `SITE` usa o preço de site de quem vende no site e o de balcão no resto
/// (Q-309, item acrescentado no app).
internal sealed class FixedMenuItems(FixedMenuSnapshot menu, ItemResolution context, bool siteOnly = false)
{
    private const int NameMax = 80;
    private const int SizeNameMax = 30;
    private const int FlavorNameMax = 40;

    public IReadOnlyList<NewItem> Resolve(IReadOnlyList<OrderItemInput> inputs)
    {
        var result = new List<NewItem>(inputs.Count);
        for (var i = 0; i < inputs.Count; i++)
        {
            result.Add(ResolveLine(inputs[i], i));
        }

        return result;
    }

    /// Uma linha, com o índice dela nos campos dos erros (`items[i]...`).
    public NewItem ResolveLine(OrderItemInput input, int index)
    {
        var field = $"items[{index}]";
        return input.ItemKind switch
        {
            OrderItemKind.FixedDish => Dish(input, field),
            OrderItemKind.FixedBeverage => Beverage(input, field),
            OrderItemKind.Combo => Combo(input, field),
            OrderItemKind.GroupAddon => LaterAddon(input, field),
            _ => throw BusinessException.Validation($"{field}.itemKind", Order.KindMismatchMessage),
        };
    }

    /// Valor da linha com os filhos e os netos (ADR-0058 item 9): `unitário × quantidade × quantidade do pai × do avô`.
    public static Money LineTotal(NewItem item) =>
        (item.UnitPrice * item.Quantity)
        + Money.Sum(item.Addons.Select(child =>
            (child.UnitPrice * child.Quantity * item.Quantity)
            + Money.Sum(child.Addons.Select(grandchild => grandchild.UnitPrice * grandchild.Quantity * child.Quantity * item.Quantity))));

    private NewItem Dish(OrderItemInput input, string field)
    {
        var dish = SellableDish(input.DishId, $"{field}.dishId");
        Money price;
        FixedMenuDishSize? size = null;
        if (dish.HasSizes)
        {
            // Prato com tamanho exige o tamanho (ADR-0056 item 7).
            if (input.DishSizeId is not { } sizeId)
            {
                throw BusinessException.Validation($"{field}.dishSizeId", $"Escolha o tamanho de {dish.Name}.");
            }

            if (!dish.Sizes.TryGetValue(sizeId, out size) || !size.IsSellable)
            {
                throw Unavailable(size is null ? $"Este tamanho de {dish.Name} não está disponível." : $"{dish.Name} {size.Name} está pausado.");
            }

            EnsureOnSite(size.Price, $"{dish.Name} {size.Name}");

            price = size.Price.For(context.PriceList);
        }
        else
        {
            if (input.DishSizeId is not null)
            {
                throw BusinessException.Validation($"{field}.dishSizeId", $"{dish.Name} não tem tamanho.");
            }

            EnsureOnSite(dish.Price!, dish.Name);
            price = dish.Price!.For(context.PriceList);
        }

        var addons = GroupAddons(dish, input.GroupAddons, dish.RequiresPreparation, $"{field}.groupAddons");
        return new NewItem(
            OrderItemKind.FixedDish, null, null, null, input.Quantity, price, Truncate(dish.Name, NameMax), null,
            dish.RequiresPreparation, input.Notes, [], addons,
            new FixedMenuRefs(DishId: dish.Id, DishSizeId: size?.Id, SizeName: size is null ? null : Truncate(size.Name, SizeNameMax)));
    }

    /// Bebida avulsa (ADR-0056 itens 11 e 12): tamanho + acréscimo do sabor; com sabores, o sabor é obrigatório.
    private NewItem Beverage(OrderItemInput input, string field)
    {
        var (size, flavor) = SellableBeverage(input.BeverageSizeId, input.BeverageFlavorId, field);
        EnsureOnSite(size.Price, $"{size.TypeName} {size.Name}");
        var price = size.Price.For(context.PriceList) + (flavor?.ExtraPrice ?? Money.Zero);
        return new NewItem(
            OrderItemKind.FixedBeverage, null, null, null, input.Quantity, price, Truncate(size.TypeName, NameMax), null,
            RequiresPreparation: false, input.Notes, [], [], BeverageRefs(size, flavor));
    }

    private NewItem Combo(OrderItemInput input, string field)
    {
        if (input.ComboId is not { } comboId || !menu.Combos.TryGetValue(comboId, out var combo))
        {
            throw Unavailable("Este combo não está disponível.");
        }

        if (!combo.IsSellable)
        {
            // O motivo é o componente pausado (ADR-0056 item 23), como o app mostra ("Pausado: Batata frita P").
            var blocked = combo.Components.FirstOrDefault(c => !c.IsSellable);
            throw Unavailable(blocked is null ? $"{combo.Name} está pausado." : $"{combo.Name} está indisponível. Pausado: {blocked.Name}.");
        }

        EnsureOnSite(combo.Price, combo.Name);

        var parts = input.Parts ?? [];
        var byComponent = new Dictionary<Guid, (ComboPartInput Part, int Index)>();
        for (var k = 0; k < parts.Count; k++)
        {
            if (combo.Components.All(c => c.Id != parts[k].ComboComponentId))
            {
                throw BusinessException.Validation($"{field}.parts[{k}].comboComponentId", $"Este componente não é de {combo.Name}.");
            }

            if (!byComponent.TryAdd(parts[k].ComboComponentId, (parts[k], k)))
            {
                throw BusinessException.Validation($"{field}.parts[{k}].comboComponentId", "Informe cada componente do combo uma vez só.");
            }
        }

        var children = combo.Components
            .Select(component =>
            {
                var found = byComponent.TryGetValue(component.Id, out var entry);
                var partField = found ? $"{field}.parts[{entry.Index}]" : $"{field}.parts";
                return component.Kind == ComboComponentKind.Dish
                    ? DishPart(component, found ? entry.Part : null, partField)
                    : BeveragePart(component, found ? entry.Part : null, partField);
            })
            .ToList();

        return new NewItem(
            OrderItemKind.Combo, null, null, null, input.Quantity, combo.Price.For(context.PriceList), Truncate(combo.Name, NameMax), null,
            RequiresPreparation: false, Notes: null, [], children, new FixedMenuRefs(ComboId: combo.Id));
    }

    /// Parte de prato: o prato no tamanho fixo do componente, `0.00`, com adicionais e observação próprios (ADR-0056 item 14).
    private NewItem DishPart(FixedMenuComboComponent component, ComboPartInput? part, string field)
    {
        var dish = menu.Dishes[component.DishId!.Value];
        if (part?.BeverageFlavorId is not null)
        {
            throw BusinessException.Validation($"{field}.beverageFlavorId", $"{dish.Name} não tem sabor.");
        }

        var size = component.DishSizeId is { } sizeId ? dish.Sizes[sizeId] : null;
        var addons = GroupAddons(dish, part?.GroupAddons, dish.RequiresPreparation, $"{field}.groupAddons");
        return new NewItem(
            OrderItemKind.ComboPart, null, null, null, component.Quantity, Money.Zero, Truncate(dish.Name, NameMax), null,
            dish.RequiresPreparation, part?.Notes, [], addons,
            new FixedMenuRefs(DishId: dish.Id, DishSizeId: size?.Id, SizeName: size is null ? null : Truncate(size.Name, SizeNameMax)));
    }

    /// Parte de bebida: o sabor é escolhido no pedido; custa só o acréscimo do sabor. Não vai à cozinha.
    private NewItem BeveragePart(FixedMenuComboComponent component, ComboPartInput? part, string field)
    {
        if (part is not null && (part.Notes is not null || part.GroupAddons is { Count: > 0 }))
        {
            throw BusinessException.Validation(field, "Na bebida do combo só se escolhe o sabor.");
        }

        var (size, flavor) = SellableBeverage(component.BeverageSizeId, part?.BeverageFlavorId, field);
        return new NewItem(
            OrderItemKind.ComboPart, null, null, null, component.Quantity, flavor?.ExtraPrice ?? Money.Zero, Truncate(size.TypeName, NameMax), null,
            RequiresPreparation: false, null, [], [], BeverageRefs(size, flavor));
    }

    /// `POST /orders/{id}/items` com `parentOrderItemId`: adicional de grupo num prato avulso ou numa parte de prato já
    /// gravada (ADR-0058 item 10). O pai e a combinação são conferidos de novo pelo `Order.AddItems`.
    private NewItem LaterAddon(OrderItemInput input, string field)
    {
        var parent = (context.Existing ?? []).SingleOrDefault(x => x.Id == input.ParentOrderItemId && x.Status == LineStatus.Active);
        if (parent is not { DishId: { } dishId } || parent.ItemKind is not (OrderItemKind.FixedDish or OrderItemKind.ComboPart))
        {
            throw BusinessException.Validation($"{field}.parentOrderItemId", "Adicional vinculado só a um prato ativo deste pedido.");
        }

        if (!menu.Dishes.TryGetValue(dishId, out var dish))
        {
            throw Unavailable("Este prato não está mais no cardápio.");
        }

        var option = Option(dish, input.AddonOptionId, input.Quantity, parent.RequiresPreparation, $"{field}.addonOptionId");
        return option with { ParentOrderItemId = parent.Id };
    }

    private List<NewItem> GroupAddons(FixedMenuDish dish, IReadOnlyList<GroupAddonInput>? inputs, bool requiresPreparation, string field) =>
        (inputs ?? []).Select((a, j) => Option(dish, a.AddonOptionId, a.Quantity, requiresPreparation, $"{field}[{j}].addonOptionId")).ToList();

    /// Adicional = opção dos grupos ligados à categoria do prato ou ao próprio prato (ADR-0056 item 18), pelo mesmo preço
    /// em qualquer tamanho e tabela (Q-308). Vai à cozinha com o pai.
    private NewItem Option(FixedMenuDish dish, Guid? optionId, int quantity, bool requiresPreparation, string field)
    {
        if (optionId is not { } id || !menu.AddonOptions.TryGetValue(id, out var option))
        {
            throw Unavailable("Este adicional não está disponível.");
        }

        if (!dish.AddonOptionIds.Contains(id))
        {
            throw BusinessException.Validation(field, $"{option.Name} não é adicional de {dish.Name}.");
        }

        if (!option.IsSellable)
        {
            throw Unavailable($"{option.Name} está pausado.");
        }

        return new NewItem(
            OrderItemKind.GroupAddon, null, null, null, quantity, option.Price, Truncate(option.Name, NameMax), null,
            requiresPreparation, null, [], [], new FixedMenuRefs(AddonOptionId: option.Id));
    }

    private FixedMenuDish SellableDish(Guid? dishId, string field)
    {
        if (dishId is not { } id)
        {
            throw BusinessException.Validation(field, "Escolha o prato.");
        }

        if (!menu.Dishes.TryGetValue(id, out var dish))
        {
            throw Unavailable("Este prato não está disponível.");
        }

        return dish.IsSellable ? dish : throw Unavailable($"{dish.Name} está pausado.");
    }

    private (FixedMenuBeverageSize Size, FixedMenuBeverageFlavor? Flavor) SellableBeverage(Guid? sizeId, Guid? flavorId, string field)
    {
        if (sizeId is not { } id || !menu.BeverageSizes.TryGetValue(id, out var size))
        {
            throw Unavailable("Esta bebida não está disponível.");
        }

        var name = $"{size.TypeName} {size.Name}";
        if (!size.IsSellable)
        {
            throw Unavailable($"{name} está pausado.");
        }

        if (size.Flavors.Count == 0)
        {
            return flavorId is null ? (size, null) : throw BusinessException.Validation($"{field}.beverageFlavorId", $"{name} não tem sabores.");
        }

        if (flavorId is not { } chosen)
        {
            throw BusinessException.Validation($"{field}.beverageFlavorId", $"Escolha o sabor de {name}.");
        }

        if (!size.Flavors.TryGetValue(chosen, out var flavor) || !flavor.IsSellable)
        {
            throw Unavailable(flavor is null ? $"Este sabor de {name} não está disponível." : $"{name} · {flavor.Name} está pausado.");
        }

        return (size, flavor);
    }

    private static FixedMenuRefs BeverageRefs(FixedMenuBeverageSize size, FixedMenuBeverageFlavor? flavor) => new(
        BeverageSizeId: size.Id,
        BeverageFlavorId: flavor?.Id,
        SizeName: Truncate(size.Name, SizeNameMax),
        FlavorName: flavor is null ? null : Truncate(flavor.Name, FlavorNameMax));

    /// Página: a unidade pedida precisa vender no site (ADR-0057 item 3). Para a página, é como se estivesse indisponível.
    private void EnsureOnSite(FixedMenuPrice price, string name)
    {
        if (siteOnly && !(price.SellsOnSite && price.Site is not null))
        {
            throw Unavailable($"{name} não é vendido pelo site.");
        }
    }

    /// `422 ProductUnavailable` (06 §6.1; o mesmo código da marmita, ADR-0056 item 24 complementado).
    private static BusinessException Unavailable(string detail) => new(ErrorCodes.ProductUnavailable, detail);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
