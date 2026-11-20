using AlmoSeis.Modules.Storefront.Contracts;
using AlmoSeis.Modules.Storefront.Domain;
using AlmoSeis.SharedKernel.Domain;
using FluentValidation;

namespace AlmoSeis.Modules.Storefront.Application;

/// Formato do PUT /storefront (06 §6.18). Regras que dependem de estado (slug livre, tamanho ativo, endereço para ativar)
/// ficam no handler e no agregado.
internal sealed class UpdateStorefrontRequestValidator : AbstractValidator<UpdateStorefrontRequest>
{
    public const string PhonePattern = @"^\+[1-9][0-9]{7,14}$";

    public UpdateStorefrontRequestValidator()
    {
        RuleFor(x => x.Slug).NotEmpty().WithMessage("Informe o endereço da página.");
        RuleFor(x => x.PublicName).NotEmpty().MinimumLength(StorefrontSettings.PublicNameMin).MaximumLength(StorefrontSettings.PublicNameMax)
            .Must(n => n is null || n.Trim().Length >= StorefrontSettings.PublicNameMin).WithMessage("Use de 2 a 80 caracteres.");
        RuleFor(x => x.ContactPhone).Matches(PhonePattern).WithMessage("Telefone no formato internacional, ex.: +5534999991234.");
        RuleFor(x => x.HoursText).MaximumLength(120);
        RuleFor(x => x.Address).NotNull();
        RuleFor(x => x.Address.Street).MaximumLength(120).When(x => x.Address is not null);
        RuleFor(x => x.Address.Number).MaximumLength(10).When(x => x.Address is not null);
        RuleFor(x => x.Address.Complement).MaximumLength(60).When(x => x.Address is not null);
        RuleFor(x => x.Address.Neighborhood).MaximumLength(60).When(x => x.Address is not null);
        RuleFor(x => x.Address.City).MaximumLength(80).When(x => x.Address is not null);
        RuleFor(x => x.Address.State).Matches("^[A-Za-z]{2}$").WithMessage("UF com 2 letras, ex.: MG.").When(x => x.Address is not null);
        RuleFor(x => x.Address.ZipCode).Matches(@"^[0-9]{5}-?[0-9]{3}$").WithMessage("CEP com 8 dígitos.").When(x => x.Address is not null);
        RuleFor(x => x.Delivery).NotNull();
        RuleFor(x => x.Delivery.PricePerKm).Must(p => p is null || (p.Value > Money.Zero && p.Value.Amount < 100_000_000m))
            .WithMessage("O preço por km deve ser maior que zero.").When(x => x.Delivery is not null);
        RuleFor(x => x.Delivery.MinimumFee).Must(m => m >= Money.Zero).WithMessage("A taxa mínima não pode ser negativa.").When(x => x.Delivery is not null);
        RuleFor(x => x.Delivery.MaxDistanceKm).Matches(@"^[0-9]{1,3}(\.[0-9])?$").WithMessage("Distância em km com até uma casa, ex.: \"6.0\".")
            .Must(km => km is null || StorefrontDtos.ParseKm(km) > 0).WithMessage("A distância máxima deve ser maior que zero.")
            .When(x => x.Delivery is not null);
    }
}
