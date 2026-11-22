using AlmoSeis.Modules.Catalog.Contracts;
using AlmoSeis.SharedKernel.Domain;
using FluentValidation;

namespace AlmoSeis.Modules.Catalog.Application;

/// Formatos do cardápio fixo (05 §5.17 0009, 06 §6.5). Regras que dependem do estado (tamanho × preço único, nome único,
/// componente ativo, uso em combo) ficam no domínio e nos handlers.
internal static class FixedMenuLimits
{
    public const int MaxSizes = 20;
    public const int MaxFlavors = 50;
    public const int MaxComponents = 10;
    public const int MaxOptions = 50;
    public const int MaxLinks = 100;
    public const int MaxQuantity = 10;

    public const string PositivePrice = "O preço deve ser maior que zero.";
    public const string NonNegativePrice = "O valor não pode ser negativo.";

    public static bool Positive(Money? price) => price is null || price.Value > Money.Zero;
}

internal sealed class DishSizeInputValidator : AbstractValidator<DishSizeInput>
{
    public DishSizeInputValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Informe o nome do tamanho.").MaximumLength(30);
        RuleFor(x => x.Price).Must(p => p > Money.Zero).WithMessage(FixedMenuLimits.PositivePrice);
        RuleFor(x => x.SitePrice).Must(FixedMenuLimits.Positive).WithMessage(FixedMenuLimits.PositivePrice);
        RuleFor(x => x.SortOrder).GreaterThanOrEqualTo(0);
    }
}

internal sealed class BeverageFlavorInputValidator : AbstractValidator<BeverageFlavorInput>
{
    public BeverageFlavorInputValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Informe o sabor.").MaximumLength(40);
        RuleFor(x => x.ExtraPrice).Must(p => p >= Money.Zero).WithMessage(FixedMenuLimits.NonNegativePrice);
        RuleFor(x => x.SortOrder).GreaterThanOrEqualTo(0);
    }
}

internal sealed class BeverageSizeInputValidator : AbstractValidator<BeverageSizeInput>
{
    public BeverageSizeInputValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Informe o tamanho.").MaximumLength(30);
        RuleFor(x => x.Price).Must(p => p > Money.Zero).WithMessage(FixedMenuLimits.PositivePrice);
        RuleFor(x => x.SitePrice).Must(FixedMenuLimits.Positive).WithMessage(FixedMenuLimits.PositivePrice);
        RuleFor(x => x.SortOrder).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Flavors).Must(f => f is null || f.Count <= FixedMenuLimits.MaxFlavors)
            .WithMessage($"Até {FixedMenuLimits.MaxFlavors} sabores por tamanho.");
        RuleForEach(x => x.Flavors).SetValidator(new BeverageFlavorInputValidator());
    }
}

internal sealed class AddonOptionInputValidator : AbstractValidator<AddonOptionInput>
{
    public AddonOptionInputValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Informe o nome da opção.").MaximumLength(80);
        RuleFor(x => x.Price).Must(p => p >= Money.Zero).WithMessage(FixedMenuLimits.NonNegativePrice);
        RuleFor(x => x.SortOrder).GreaterThanOrEqualTo(0);
    }
}

/// Prato no tamanho fixo **ou** tamanho de bebida (`ck_combo_components_target`), de 1 a 10.
internal sealed class ComboComponentInputValidator : AbstractValidator<ComboComponentInput>
{
    public ComboComponentInputValidator()
    {
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.Quantity).InclusiveBetween(1, FixedMenuLimits.MaxQuantity).WithMessage($"Deve ser entre 1 e {FixedMenuLimits.MaxQuantity}.");
        RuleFor(x => x.DishId).NotNull().WithMessage("Escolha o prato.").When(x => x.Kind == ComboComponentKind.Dish);
        RuleFor(x => x.BeverageSizeId).Null().WithMessage("Componente de prato não tem bebida.").When(x => x.Kind == ComboComponentKind.Dish);
        RuleFor(x => x.BeverageSizeId).NotNull().WithMessage("Escolha o tamanho da bebida.").When(x => x.Kind == ComboComponentKind.Beverage);
        RuleFor(x => x.DishId).Null().WithMessage("Componente de bebida não tem prato.").When(x => x.Kind == ComboComponentKind.Beverage);
        RuleFor(x => x.DishSizeId).Null().WithMessage("Componente de bebida não tem prato.").When(x => x.Kind == ComboComponentKind.Beverage);
    }
}

internal sealed class CreateDishCategoryCommandValidator : AbstractValidator<CreateDishCategoryCommand>
{
    public CreateDishCategoryCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Informe o nome.").MaximumLength(40);
        RuleFor(x => x.SortOrder).GreaterThanOrEqualTo(0);
    }
}

internal sealed class UpdateDishCategoryRequestValidator : AbstractValidator<UpdateDishCategoryRequest>
{
    public UpdateDishCategoryRequestValidator()
    {
        RuleFor(x => x.Name.Value).NotEmpty().WithMessage("Informe o nome.").MaximumLength(40).When(x => x.Name.HasValue).OverridePropertyName("name");
        RuleFor(x => x.SortOrder.Value).GreaterThanOrEqualTo(0).When(x => x.SortOrder.HasValue).OverridePropertyName("sortOrder");
    }
}

internal sealed class CreateDishCommandValidator : AbstractValidator<CreateDishCommand>
{
    public CreateDishCommandValidator()
    {
        RuleFor(x => x.DishCategoryId).NotEmpty().WithMessage("Escolha a categoria.");
        RuleFor(x => x.Name).NotEmpty().WithMessage("Informe o nome.").MaximumLength(80);
        RuleFor(x => x.Description).MaximumLength(300);
        RuleFor(x => x.Price).Must(FixedMenuLimits.Positive).WithMessage(FixedMenuLimits.PositivePrice);
        RuleFor(x => x.SitePrice).Must(FixedMenuLimits.Positive).WithMessage(FixedMenuLimits.PositivePrice);
        RuleFor(x => x.SortOrder).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Sizes).Must(s => s is null || s.Count <= FixedMenuLimits.MaxSizes).WithMessage($"Até {FixedMenuLimits.MaxSizes} tamanhos.");
        RuleForEach(x => x.Sizes).SetValidator(new DishSizeInputValidator());
    }
}

internal sealed class UpdateDishRequestValidator : AbstractValidator<UpdateDishRequest>
{
    public UpdateDishRequestValidator()
    {
        RuleFor(x => x.DishCategoryId.Value).NotEmpty().When(x => x.DishCategoryId.HasValue).OverridePropertyName("dishCategoryId");
        RuleFor(x => x.Name.Value).NotEmpty().WithMessage("Informe o nome.").MaximumLength(80).When(x => x.Name.HasValue).OverridePropertyName("name");
        RuleFor(x => x.Description.Value).MaximumLength(300).When(x => x.Description.HasValue).OverridePropertyName("description");
        RuleFor(x => x.Price.Value).Must(FixedMenuLimits.Positive).WithMessage(FixedMenuLimits.PositivePrice)
            .When(x => x.Price.HasValue).OverridePropertyName("price");
        RuleFor(x => x.SitePrice.Value).Must(FixedMenuLimits.Positive).WithMessage(FixedMenuLimits.PositivePrice)
            .When(x => x.SitePrice.HasValue).OverridePropertyName("sitePrice");
        RuleFor(x => x.SortOrder.Value).GreaterThanOrEqualTo(0).When(x => x.SortOrder.HasValue).OverridePropertyName("sortOrder");
        RuleFor(x => x.ImageStorageKey.Value).MaximumLength(200).When(x => x.ImageStorageKey.HasValue).OverridePropertyName("imageStorageKey");
        RuleFor(x => x.Sizes.Value).NotNull().WithMessage("Envie a lista (vazia para nenhum).").Must(s => s is null || s.Count <= FixedMenuLimits.MaxSizes).WithMessage($"Até {FixedMenuLimits.MaxSizes} tamanhos.")
            .When(x => x.Sizes.HasValue).OverridePropertyName("sizes");
        RuleForEach(x => x.Sizes.Value).SetValidator(new DishSizeInputValidator()).When(x => x.Sizes.HasValue).OverridePropertyName("sizes");
    }
}

internal sealed class CreateBeverageTypeCommandValidator : AbstractValidator<CreateBeverageTypeCommand>
{
    public CreateBeverageTypeCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Informe o nome.").MaximumLength(40);
        RuleFor(x => x.SortOrder).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Sizes).Must(s => s is null || s.Count <= FixedMenuLimits.MaxSizes).WithMessage($"Até {FixedMenuLimits.MaxSizes} tamanhos.");
        RuleForEach(x => x.Sizes).SetValidator(new BeverageSizeInputValidator());
    }
}

internal sealed class UpdateBeverageTypeRequestValidator : AbstractValidator<UpdateBeverageTypeRequest>
{
    public UpdateBeverageTypeRequestValidator()
    {
        RuleFor(x => x.Name.Value).NotEmpty().WithMessage("Informe o nome.").MaximumLength(40).When(x => x.Name.HasValue).OverridePropertyName("name");
        RuleFor(x => x.SortOrder.Value).GreaterThanOrEqualTo(0).When(x => x.SortOrder.HasValue).OverridePropertyName("sortOrder");
        RuleFor(x => x.Sizes.Value).NotNull().WithMessage("Envie a lista (vazia para nenhum).").Must(s => s is null || s.Count <= FixedMenuLimits.MaxSizes).WithMessage($"Até {FixedMenuLimits.MaxSizes} tamanhos.")
            .When(x => x.Sizes.HasValue).OverridePropertyName("sizes");
        RuleForEach(x => x.Sizes.Value).SetValidator(new BeverageSizeInputValidator()).When(x => x.Sizes.HasValue).OverridePropertyName("sizes");
    }
}

internal sealed class CreateComboCommandValidator : AbstractValidator<CreateComboCommand>
{
    public CreateComboCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Informe o nome.").MaximumLength(80);
        RuleFor(x => x.Description).MaximumLength(300);
        RuleFor(x => x.Price).Must(p => p > Money.Zero).WithMessage(FixedMenuLimits.PositivePrice);
        RuleFor(x => x.SitePrice).Must(FixedMenuLimits.Positive).WithMessage(FixedMenuLimits.PositivePrice);
        RuleFor(x => x.SortOrder).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Components).NotEmpty().WithMessage("O combo precisa de pelo menos um item.")
            .Must(c => c is null || c.Count <= FixedMenuLimits.MaxComponents).WithMessage($"Até {FixedMenuLimits.MaxComponents} itens.");
        RuleForEach(x => x.Components).SetValidator(new ComboComponentInputValidator());
    }
}

internal sealed class UpdateComboRequestValidator : AbstractValidator<UpdateComboRequest>
{
    public UpdateComboRequestValidator()
    {
        RuleFor(x => x.Name.Value).NotEmpty().WithMessage("Informe o nome.").MaximumLength(80).When(x => x.Name.HasValue).OverridePropertyName("name");
        RuleFor(x => x.Description.Value).MaximumLength(300).When(x => x.Description.HasValue).OverridePropertyName("description");
        RuleFor(x => x.Price.Value).Must(p => p > Money.Zero).WithMessage(FixedMenuLimits.PositivePrice)
            .When(x => x.Price.HasValue).OverridePropertyName("price");
        RuleFor(x => x.SitePrice.Value).Must(FixedMenuLimits.Positive).WithMessage(FixedMenuLimits.PositivePrice)
            .When(x => x.SitePrice.HasValue).OverridePropertyName("sitePrice");
        RuleFor(x => x.SortOrder.Value).GreaterThanOrEqualTo(0).When(x => x.SortOrder.HasValue).OverridePropertyName("sortOrder");
        RuleFor(x => x.ImageStorageKey.Value).MaximumLength(200).When(x => x.ImageStorageKey.HasValue).OverridePropertyName("imageStorageKey");
        RuleFor(x => x.Components.Value).NotNull().NotEmpty().WithMessage("O combo precisa de pelo menos um item.")
            .Must(c => c is null || c.Count <= FixedMenuLimits.MaxComponents).WithMessage($"Até {FixedMenuLimits.MaxComponents} itens.")
            .When(x => x.Components.HasValue).OverridePropertyName("components");
        RuleForEach(x => x.Components.Value).SetValidator(new ComboComponentInputValidator())
            .When(x => x.Components.HasValue).OverridePropertyName("components");
    }
}

internal sealed class CreateAddonGroupCommandValidator : AbstractValidator<CreateAddonGroupCommand>
{
    public CreateAddonGroupCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Informe o nome.").MaximumLength(60);
        RuleFor(x => x.SortOrder).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Options).Must(o => o is null || o.Count <= FixedMenuLimits.MaxOptions).WithMessage($"Até {FixedMenuLimits.MaxOptions} opções.");
        RuleForEach(x => x.Options).SetValidator(new AddonOptionInputValidator());
        RuleFor(x => x.DishCategoryIds).Must(l => l is null || l.Count <= FixedMenuLimits.MaxLinks);
        RuleFor(x => x.DishIds).Must(l => l is null || l.Count <= FixedMenuLimits.MaxLinks);
    }
}

internal sealed class UpdateAddonGroupRequestValidator : AbstractValidator<UpdateAddonGroupRequest>
{
    public UpdateAddonGroupRequestValidator()
    {
        RuleFor(x => x.Name.Value).NotEmpty().WithMessage("Informe o nome.").MaximumLength(60).When(x => x.Name.HasValue).OverridePropertyName("name");
        RuleFor(x => x.SortOrder.Value).GreaterThanOrEqualTo(0).When(x => x.SortOrder.HasValue).OverridePropertyName("sortOrder");
        RuleFor(x => x.Options.Value).NotNull().WithMessage("Envie a lista (vazia para nenhum).").Must(o => o is null || o.Count <= FixedMenuLimits.MaxOptions).WithMessage($"Até {FixedMenuLimits.MaxOptions} opções.")
            .When(x => x.Options.HasValue).OverridePropertyName("options");
        RuleForEach(x => x.Options.Value).SetValidator(new AddonOptionInputValidator()).When(x => x.Options.HasValue).OverridePropertyName("options");
        RuleFor(x => x.DishCategoryIds.Value).NotNull().WithMessage("Envie a lista (vazia para nenhum).").Must(l => l is null || l.Count <= FixedMenuLimits.MaxLinks)
            .When(x => x.DishCategoryIds.HasValue).OverridePropertyName("dishCategoryIds");
        RuleFor(x => x.DishIds.Value).NotNull().WithMessage("Envie a lista (vazia para nenhum).").Must(l => l is null || l.Count <= FixedMenuLimits.MaxLinks).When(x => x.DishIds.HasValue).OverridePropertyName("dishIds");
    }
}
