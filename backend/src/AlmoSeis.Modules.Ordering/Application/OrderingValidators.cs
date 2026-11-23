using AlmoSeis.Modules.Ordering.Contracts;
using FluentValidation;

namespace AlmoSeis.Modules.Ordering.Application;

/// Formatos de 05 §5.7/§5.9. Regras que dependem de estado (cardápio, modalidade, pago) ficam no domínio/handler.
internal static class OrderingFormats
{
    /// E.164, igual ao CHECK de customers.phone_number e orders.customer_phone.
    public const string PhonePattern = @"^\+[1-9][0-9]{7,14}$";
    public const string PhoneMessage = "Telefone no formato internacional, ex.: +5534999991234.";

    /// Telefone do pedido do app (`customerPhone`) e da busca do cliente (`POST /customers/lookup`): o número brasileiro
    /// passa pelo mesmo `BrazilianPhone.Normalize` da página de pedidos, para "(34) 99999-1234", "34999991234",
    /// "+55 34 99999-1234" e "+5534999991234" virarem o mesmo cliente (06 §6.8 e §6.9). Outro E.164 (estrangeiro, ou
    /// `+55` fora da regra brasileira) segue como veio, como antes. Fora dos dois → `null` (`422`).
    public static string? NormalizePhone(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        if (BrazilianPhone.Normalize(input) is { } brazilian)
        {
            return brazilian;
        }

        var trimmed = input.Trim();
        return System.Text.RegularExpressions.Regex.IsMatch(trimmed, PhonePattern) ? trimmed : null;
    }
}

internal sealed class DeliveryAddressValidator : AbstractValidator<DeliveryAddressDto>
{
    public DeliveryAddressValidator()
    {
        RuleFor(x => x.Street).NotEmpty().WithMessage("Informe a rua.").MaximumLength(120);
        RuleFor(x => x.Number).MaximumLength(10);
        RuleFor(x => x.Complement).MaximumLength(60);
        RuleFor(x => x.Neighborhood).MaximumLength(60);
        RuleFor(x => x.Reference).MaximumLength(120);
    }
}

internal sealed class OrderItemInputValidator : AbstractValidator<OrderItemInput>
{
    public OrderItemInputValidator()
    {
        RuleFor(x => x.ItemKind).IsInEnum();
        RuleFor(x => x.Quantity).InclusiveBetween(1, 99).WithMessage("A quantidade deve estar entre 1 e 99.");
        RuleFor(x => x.Notes).MaximumLength(200);
        RuleForEach(x => x.Addons).ChildRules(a =>
        {
            a.RuleFor(x => x.Quantity).InclusiveBetween(1, 99).WithMessage("A quantidade deve estar entre 1 e 99.");
            a.RuleFor(x => x.Notes).MaximumLength(200);
        });

        // Cardápio fixo (ADR-0058 item 8): cada tipo só com os seus campos. Disponibilidade, tamanho, sabor e grupo do
        // adicional dependem do cardápio e ficam no resolvedor.
        var fixedKinds = new[] { OrderItemKind.FixedDish, OrderItemKind.FixedBeverage, OrderItemKind.Combo, OrderItemKind.GroupAddon };
        RuleFor(x => x.ItemKind).NotEqual(OrderItemKind.ComboPart).WithMessage("As partes do combo vão em parts.");
        When(x => fixedKinds.Contains(x.ItemKind), () =>
        {
            RuleFor(x => x.ProductId).Null().WithMessage("Item do cardápio fixo não usa productId.");
            RuleFor(x => x.MarmitaSizeId).Null();
            RuleFor(x => x.MainDishProductIds).Must(l => l is null || l.Count == 0);
            RuleFor(x => x.SideDishProductIds).Must(l => l is null || l.Count == 0);
            RuleFor(x => x.Addons).Must(l => l is null || l.Count == 0).WithMessage("Use groupAddons nos itens do cardápio fixo.");
        });
        When(x => !fixedKinds.Contains(x.ItemKind), () =>
        {
            RuleFor(x => x).Must(x => x.DishId is null && x.DishSizeId is null && x.BeverageSizeId is null && x.BeverageFlavorId is null
                    && x.ComboId is null && x.AddonOptionId is null && (x.GroupAddons is null || x.GroupAddons.Count == 0)
                    && (x.Parts is null || x.Parts.Count == 0))
                .WithMessage("Campos do cardápio fixo só em itens do cardápio fixo.").OverridePropertyName("itemKind");
        });
        When(x => x.ItemKind == OrderItemKind.FixedDish, () =>
        {
            RuleFor(x => x.DishId).NotNull().WithMessage("Escolha o prato.");
            RuleFor(x => new { x.BeverageSizeId, x.BeverageFlavorId, x.ComboId, x.AddonOptionId })
                .Must(r => r.BeverageSizeId is null && r.BeverageFlavorId is null && r.ComboId is null && r.AddonOptionId is null)
                .WithMessage("Prato avulso usa dishId, dishSizeId e groupAddons.").OverridePropertyName("itemKind");
            RuleFor(x => x.Parts).Must(l => l is null || l.Count == 0);
        });
        When(x => x.ItemKind == OrderItemKind.FixedBeverage, () =>
        {
            RuleFor(x => x.BeverageSizeId).NotNull().WithMessage("Escolha a bebida.");
            RuleFor(x => new { x.DishId, x.DishSizeId, x.ComboId, x.AddonOptionId })
                .Must(r => r.DishId is null && r.DishSizeId is null && r.ComboId is null && r.AddonOptionId is null)
                .WithMessage("Bebida usa beverageSizeId e beverageFlavorId.").OverridePropertyName("itemKind");
            RuleFor(x => x.GroupAddons).Must(l => l is null || l.Count == 0).WithMessage("Bebida não tem adicionais.");
            RuleFor(x => x.Parts).Must(l => l is null || l.Count == 0);
        });
        When(x => x.ItemKind == OrderItemKind.Combo, () =>
        {
            RuleFor(x => x.ComboId).NotNull().WithMessage("Escolha o combo.");
            RuleFor(x => new { x.DishId, x.DishSizeId, x.BeverageSizeId, x.BeverageFlavorId, x.AddonOptionId })
                .Must(r => r.DishId is null && r.DishSizeId is null && r.BeverageSizeId is null && r.BeverageFlavorId is null && r.AddonOptionId is null)
                .WithMessage("Combo usa comboId e parts.").OverridePropertyName("itemKind");
            RuleFor(x => x.Notes).Null().WithMessage("A observação vai em cada prato do combo (parts[].notes).");
            RuleFor(x => x.GroupAddons).Must(l => l is null || l.Count == 0).WithMessage("Os adicionais vão em cada prato do combo (parts[].groupAddons).");
            RuleForEach(x => x.Parts).ChildRules(p =>
            {
                p.RuleFor(x => x.ComboComponentId).NotEmpty().WithMessage("Informe o componente do combo.");
                p.RuleFor(x => x.Notes).MaximumLength(200);
                p.RuleForEach(x => x.GroupAddons).SetValidator(new GroupAddonInputValidator());
            });
        });
        When(x => x.ItemKind == OrderItemKind.GroupAddon, () =>
        {
            RuleFor(x => x.ParentOrderItemId).NotNull().WithMessage("Adicional de grupo vai num prato do pedido.");
            RuleFor(x => x.AddonOptionId).NotNull().WithMessage("Escolha o adicional.");
            RuleFor(x => x.Quantity).InclusiveBetween(1, GroupAddonInputValidator.MaxQuantity).WithMessage(GroupAddonInputValidator.QuantityMessage);
            RuleFor(x => new { x.DishId, x.DishSizeId, x.BeverageSizeId, x.BeverageFlavorId, x.ComboId })
                .Must(r => r.DishId is null && r.DishSizeId is null && r.BeverageSizeId is null && r.BeverageFlavorId is null && r.ComboId is null)
                .WithMessage("Adicional de grupo usa parentOrderItemId e addonOptionId.").OverridePropertyName("itemKind");
            RuleFor(x => x.Notes).Null().WithMessage("A observação vai no prato.");
            RuleFor(x => x.GroupAddons).Must(l => l is null || l.Count == 0);
            RuleFor(x => x.Parts).Must(l => l is null || l.Count == 0);
        });
        RuleForEach(x => x.GroupAddons).SetValidator(new GroupAddonInputValidator());
    }
}

/// ADR-0056 item 19: qualquer quantidade de 1 a 10 por opção, sem mínimo nem máximo por grupo nesta fase.
internal sealed class GroupAddonInputValidator : AbstractValidator<GroupAddonInput>
{
    public const int MaxQuantity = 10;
    public const string QuantityMessage = "A quantidade do adicional deve estar entre 1 e 10.";

    public GroupAddonInputValidator()
    {
        RuleFor(x => x.AddonOptionId).NotEmpty().WithMessage("Escolha o adicional.");
        RuleFor(x => x.Quantity).InclusiveBetween(1, MaxQuantity).WithMessage(QuantityMessage);
    }
}

internal sealed class CreateOrderCommandValidator : AbstractValidator<CreateOrderCommand>
{
    public CreateOrderCommandValidator()
    {
        RuleFor(x => x.OrderType).IsInEnum();
        RuleFor(x => x.FulfillmentType).IsInEnum();
        RuleFor(x => x.CustomerName).MaximumLength(80);
        RuleFor(x => x.CustomerPhone).Must(p => p is null || OrderingFormats.NormalizePhone(p) is not null).WithMessage(OrderingFormats.PhoneMessage);
        RuleFor(x => x.DeliveryAddress!).SetValidator(new DeliveryAddressValidator()).When(x => x.DeliveryAddress is not null);
        RuleFor(x => x.TableLabel).MaximumLength(10);
        RuleFor(x => x.TabNumber).MaximumLength(10);
        RuleFor(x => x.Notes).MaximumLength(300);
        RuleFor(x => x.ExpectedPaymentMethod).IsInEnum().When(x => x.ExpectedPaymentMethod is not null);
        RuleFor(x => x.Items).NotNull().WithMessage("Informe os itens (pode ser uma lista vazia em pedido por quilo).");
        RuleForEach(x => x.Items).SetValidator(new OrderItemInputValidator());
    }
}

internal sealed class AddOrderItemsRequestValidator : AbstractValidator<AddOrderItemsRequest>
{
    public AddOrderItemsRequestValidator()
    {
        RuleFor(x => x.Items).NotEmpty().WithMessage("Informe pelo menos um item.");
        RuleForEach(x => x.Items).SetValidator(new OrderItemInputValidator());
    }
}

internal sealed class UpdateOrderRequestValidator : AbstractValidator<UpdateOrderRequest>
{
    public UpdateOrderRequestValidator()
    {
        RuleFor(x => x.CustomerName.Value).MaximumLength(80).When(x => x.CustomerName.HasValue).OverridePropertyName("customerName");
        RuleFor(x => x.CustomerPhone.Value).Must(p => p is null || OrderingFormats.NormalizePhone(p) is not null).WithMessage(OrderingFormats.PhoneMessage)
            .When(x => x.CustomerPhone.HasValue).OverridePropertyName("customerPhone");
        RuleFor(x => x.FulfillmentType.Value).IsInEnum().When(x => x.FulfillmentType.HasValue).OverridePropertyName("fulfillmentType");
        RuleFor(x => x.DeliveryAddress.Value!).SetValidator(new DeliveryAddressValidator())
            .When(x => x.DeliveryAddress is { HasValue: true, Value: not null }).OverridePropertyName("deliveryAddress");
        RuleFor(x => x.TableLabel.Value).MaximumLength(10).When(x => x.TableLabel.HasValue).OverridePropertyName("tableLabel");
        RuleFor(x => x.TabNumber.Value).MaximumLength(10).When(x => x.TabNumber.HasValue).OverridePropertyName("tabNumber");
        RuleFor(x => x.Notes.Value).MaximumLength(300).When(x => x.Notes.HasValue).OverridePropertyName("notes");
    }
}

internal sealed class UpdateOrderItemRequestValidator : AbstractValidator<UpdateOrderItemRequest>
{
    public UpdateOrderItemRequestValidator()
    {
        RuleFor(x => x.Quantity.Value).InclusiveBetween(1, 99).WithMessage("A quantidade deve estar entre 1 e 99.")
            .When(x => x.Quantity.HasValue).OverridePropertyName("quantity");
        RuleFor(x => x.Notes.Value).MaximumLength(200).When(x => x.Notes.HasValue).OverridePropertyName("notes");
    }
}

internal sealed class CancelOrderRequestValidator : AbstractValidator<CancelOrderRequest>
{
    public CancelOrderRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Informe o motivo.").MaximumLength(300);
    }
}

internal sealed class ChangeProductionStatusRequestValidator : AbstractValidator<ChangeProductionStatusRequest>
{
    public ChangeProductionStatusRequestValidator()
    {
        RuleFor(x => x.ProductionStatus).IsInEnum();
    }
}

internal sealed class AcknowledgeAlertsRequestValidator : AbstractValidator<AcknowledgeAlertsRequest>
{
    public AcknowledgeAlertsRequestValidator()
    {
        RuleFor(x => x.EventIds).NotEmpty().WithMessage("Informe os alertas.");
    }
}

internal sealed class CustomerAddressInputValidator : AbstractValidator<CustomerAddressInput>
{
    public CustomerAddressInputValidator()
    {
        RuleFor(x => x.Label).MaximumLength(30);
        RuleFor(x => x.Street).NotEmpty().WithMessage("Informe a rua.").MaximumLength(120);
        RuleFor(x => x.Number).MaximumLength(10);
        RuleFor(x => x.Complement).MaximumLength(60);
        RuleFor(x => x.Neighborhood).MaximumLength(60);
        RuleFor(x => x.Reference).MaximumLength(120);
    }
}

internal sealed class CreateCustomerCommandValidator : AbstractValidator<CreateCustomerCommand>
{
    public CreateCustomerCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Informe o nome.").MaximumLength(80);
        RuleFor(x => x.PhoneNumber).Matches(OrderingFormats.PhonePattern).WithMessage(OrderingFormats.PhoneMessage);
        RuleFor(x => x.Notes).MaximumLength(300);
        RuleForEach(x => x.Addresses).SetValidator(new CustomerAddressInputValidator());
    }
}

internal sealed class UpdateCustomerRequestValidator : AbstractValidator<UpdateCustomerRequest>
{
    public UpdateCustomerRequestValidator()
    {
        RuleFor(x => x.Name.Value).NotEmpty().WithMessage("Informe o nome.").MaximumLength(80).When(x => x.Name.HasValue).OverridePropertyName("name");
        RuleFor(x => x.PhoneNumber.Value).Matches(OrderingFormats.PhonePattern).WithMessage(OrderingFormats.PhoneMessage)
            .When(x => x.PhoneNumber.HasValue).OverridePropertyName("phoneNumber");
        RuleFor(x => x.Notes.Value).MaximumLength(300).When(x => x.Notes.HasValue).OverridePropertyName("notes");
    }
}

internal sealed class UpdateCustomerAddressRequestValidator : AbstractValidator<UpdateCustomerAddressRequest>
{
    public UpdateCustomerAddressRequestValidator()
    {
        RuleFor(x => x.Label.Value).MaximumLength(30).When(x => x.Label.HasValue).OverridePropertyName("label");
        RuleFor(x => x.Street.Value).NotEmpty().WithMessage("Informe a rua.").MaximumLength(120).When(x => x.Street.HasValue).OverridePropertyName("street");
        RuleFor(x => x.Number.Value).MaximumLength(10).When(x => x.Number.HasValue).OverridePropertyName("number");
        RuleFor(x => x.Complement.Value).MaximumLength(60).When(x => x.Complement.HasValue).OverridePropertyName("complement");
        RuleFor(x => x.Neighborhood.Value).MaximumLength(60).When(x => x.Neighborhood.HasValue).OverridePropertyName("neighborhood");
        RuleFor(x => x.Reference.Value).MaximumLength(120).When(x => x.Reference.HasValue).OverridePropertyName("reference");
    }
}

internal sealed class AnonymizeCustomerRequestValidator : AbstractValidator<AnonymizeCustomerRequest>
{
    public AnonymizeCustomerRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Informe o motivo.").MaximumLength(300);
    }
}

/// Tempo estimado do aceite: 15, 30, 45 ou 60 min (ADR-0046 item 6).
internal sealed class AcceptOrderRequestValidator : AbstractValidator<AcceptOrderRequest>
{
    public AcceptOrderRequestValidator()
    {
        RuleFor(x => x.EstimatedReadyMinutes)
            .Must(m => m is null || Domain.Order.EstimatedMinutesOptions.Contains(m.Value))
            .WithMessage("Escolha 15, 30, 45 ou 60 minutos.");
    }
}

/// Motivo da lista e texto de 5 a 200 (obrigatório em OTHER). `BUSINESS_DAY_CLOSED` é só do sistema (ADR-0046 itens 12 e 13).
internal sealed class RejectOrderRequestValidator : AbstractValidator<RejectOrderRequest>
{
    public RejectOrderRequestValidator()
    {
        RuleFor(x => x.ReasonCode).IsInEnum()
            .NotEqual(RejectionReason.BusinessDayClosed).WithMessage("Escolha um motivo da lista.");
        RuleFor(x => x.Message).NotEmpty().When(x => x.ReasonCode == RejectionReason.Other)
            .WithMessage("Explique o motivo para o cliente.");
        RuleFor(x => x.Message!.Trim().Length)
            .InclusiveBetween(Domain.Order.RejectionMessageMin, Domain.Order.RejectionMessageMax)
            .OverridePropertyName("message")
            .WithMessage($"Use de {Domain.Order.RejectionMessageMin} a {Domain.Order.RejectionMessageMax} caracteres.")
            .When(x => !string.IsNullOrWhiteSpace(x.Message));
    }
}

internal sealed class ChangeFulfillmentRequestValidator : AbstractValidator<ChangeFulfillmentRequest>
{
    public ChangeFulfillmentRequestValidator()
    {
        RuleFor(x => x.FulfillmentStatus).IsInEnum();
    }
}
