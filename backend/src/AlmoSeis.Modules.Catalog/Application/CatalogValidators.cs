using AlmoSeis.Modules.Catalog.Contracts;
using AlmoSeis.SharedKernel.Domain;
using FluentValidation;

namespace AlmoSeis.Modules.Catalog.Application;

/// Formatos de 05 §5.5. Regras que dependem do estado (preço × tipo, nome único) ficam no domínio/handler.
internal sealed class CreateProductCommandValidator : AbstractValidator<CreateProductCommand>
{
    public CreateProductCommandValidator()
    {
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.Name).NotEmpty().WithMessage("Informe o nome.").MaximumLength(80);
        RuleFor(x => x.Description).MaximumLength(300);
        RuleFor(x => x.Category).MaximumLength(40);
        RuleFor(x => x.DefaultPrice).Must(p => p is null || p.Value >= Money.Zero).WithMessage("O preço não pode ser negativo.");
        // Só a bebida tem preço de site, e ela pode ter 0,00 (água da casa, cafezinho: ADR-0057 item 2, revisão de
        // 2026-11-23, I6); o domínio recusa o preço de site fora de BEVERAGE.
        RuleFor(x => x.SitePrice).Must(p => p is null || p.Value >= Money.Zero).WithMessage("O preço de site não pode ser negativo.");
    }
}

internal sealed class UpdateProductRequestValidator : AbstractValidator<UpdateProductRequest>
{
    public UpdateProductRequestValidator()
    {
        RuleFor(x => x.Kind.Value).IsInEnum().When(x => x.Kind.HasValue).OverridePropertyName("kind");
        RuleFor(x => x.Name.Value).NotEmpty().WithMessage("Informe o nome.").MaximumLength(80)
            .When(x => x.Name.HasValue).OverridePropertyName("name");
        RuleFor(x => x.Description.Value).MaximumLength(300).When(x => x.Description.HasValue).OverridePropertyName("description");
        RuleFor(x => x.Category.Value).MaximumLength(40).When(x => x.Category.HasValue).OverridePropertyName("category");
        RuleFor(x => x.DefaultPrice.Value).Must(p => p is null || p.Value >= Money.Zero).WithMessage("O preço não pode ser negativo.")
            .When(x => x.DefaultPrice.HasValue).OverridePropertyName("defaultPrice");
        RuleFor(x => x.ImageStorageKey.Value).MaximumLength(200).When(x => x.ImageStorageKey.HasValue).OverridePropertyName("imageStorageKey");
        RuleFor(x => x.SitePrice.Value).Must(p => p is null || p.Value >= Money.Zero).WithMessage("O preço de site não pode ser negativo.")
            .When(x => x.SitePrice.HasValue).OverridePropertyName("sitePrice");
    }
}

internal sealed class CreateMarmitaSizeCommandValidator : AbstractValidator<CreateMarmitaSizeCommand>
{
    public CreateMarmitaSizeCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Informe o nome.").MaximumLength(20);
        RuleFor(x => x.Price).Must(p => p > Money.Zero).WithMessage("O preço deve ser maior que zero.");
        RuleFor(x => x.SortOrder).GreaterThanOrEqualTo(0);
        RuleFor(x => x.SitePrice).Must(p => p is null || p.Value > Money.Zero).WithMessage("O preço de site deve ser maior que zero.");
    }
}

internal sealed class UpdateMarmitaSizeRequestValidator : AbstractValidator<UpdateMarmitaSizeRequest>
{
    public UpdateMarmitaSizeRequestValidator()
    {
        RuleFor(x => x.Name.Value).NotEmpty().WithMessage("Informe o nome.").MaximumLength(20)
            .When(x => x.Name.HasValue).OverridePropertyName("name");
        RuleFor(x => x.Price.Value).Must(p => p > Money.Zero).WithMessage("O preço deve ser maior que zero.")
            .When(x => x.Price.HasValue).OverridePropertyName("price");
        RuleFor(x => x.SortOrder.Value).GreaterThanOrEqualTo(0).When(x => x.SortOrder.HasValue).OverridePropertyName("sortOrder");
        RuleFor(x => x.SitePrice.Value).Must(p => p is null || p.Value > Money.Zero).WithMessage("O preço de site deve ser maior que zero.")
            .When(x => x.SitePrice.HasValue).OverridePropertyName("sitePrice");
    }
}
