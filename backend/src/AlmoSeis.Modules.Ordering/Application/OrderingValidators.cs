using AlmoSeis.Modules.Ordering.Contracts;
using FluentValidation;

namespace AlmoSeis.Modules.Ordering.Application;

/// Formatos de 05 §5.7/§5.9. Regras que dependem de estado (cardápio, modalidade, pago) ficam no domínio/handler.
internal static class OrderingFormats
{
    /// E.164, igual ao CHECK de customers.phone_number e orders.customer_phone.
    public const string PhonePattern = @"^\+[1-9][0-9]{7,14}$";
    public const string PhoneMessage = "Telefone no formato internacional, ex.: +5534999991234.";
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
    }
}

internal sealed class CreateOrderCommandValidator : AbstractValidator<CreateOrderCommand>
{
    public CreateOrderCommandValidator()
    {
        RuleFor(x => x.OrderType).IsInEnum();
        RuleFor(x => x.FulfillmentType).IsInEnum();
        RuleFor(x => x.CustomerName).MaximumLength(80);
        RuleFor(x => x.CustomerPhone).Matches(OrderingFormats.PhonePattern).WithMessage(OrderingFormats.PhoneMessage);
        RuleFor(x => x.DeliveryAddress!).SetValidator(new DeliveryAddressValidator()).When(x => x.DeliveryAddress is not null);
        RuleFor(x => x.TableLabel).MaximumLength(10);
        RuleFor(x => x.TabNumber).MaximumLength(10);
        RuleFor(x => x.Notes).MaximumLength(300);
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
        RuleFor(x => x.CustomerPhone.Value).Matches(OrderingFormats.PhonePattern).WithMessage(OrderingFormats.PhoneMessage)
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
