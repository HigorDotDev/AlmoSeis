using AlmoSeis.Infrastructure.Audit;
using AlmoSeis.Infrastructure.Concurrency;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Closing.Contracts;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.Modules.Payments.Contracts;
using AlmoSeis.Modules.Payments.Domain;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Errors;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AlmoSeis.Modules.Payments.Application;

/// POST /orders/{id}/payments [I][M] (06 §6.10, 07 §7.14). Pedido de outro expediente (fiado) também:
/// o pagamento cai no expediente aberto (ADR-0015).
internal sealed class RegisterPaymentHandler(
    AppDbContext db,
    ITenantContext tenant,
    IIdGenerator ids,
    IConcurrencyContext concurrency,
    IBusinessDayReader businessDays,
    IOrderPaymentPort orders,
    ITenantSettingsReader settings,
    IAuditWriter audit,
    PaymentDtoBuilder dtos,
    TimeProvider time) : ICommandHandler<RegisterPaymentCommand, RegisterPaymentResultDto>
{
    public async Task<RegisterPaymentResultDto> HandleAsync(RegisterPaymentCommand command, CancellationToken cancellationToken)
    {
        var day = await businessDays.GetOpenAsync(cancellationToken) ?? throw new BusinessException(ErrorCodes.BusinessDayRequired);
        var order = await orders.LoadForPaymentAsync(command.OrderId, concurrency.ExpectedVersion, cancellationToken);

        var request = command.Request;
        var method = UpperSnakeEnumConverter<PaymentMethod>.ToText(request.PaymentMethod);
        if (!(await settings.GetAsync(cancellationToken)).EnabledPaymentMethods.Contains(method))
        {
            throw new BusinessException(ErrorCodes.PaymentMethodDisabled);
        }

        var payment = Payment.Register(
            ids.New(), tenant.TenantId!.Value, order.Id, day.Id, day.BusinessDate, request.Amount, request.PaymentMethod,
            request.CashReceived, request.Label, tenant.EmployeeId!.Value, time.GetUtcNow());
        var balance = await orders.ApplyPaymentAsync(order.Id, payment.Amount, cancellationToken);
        db.Add(payment);

        audit.Add("PaymentRegistered", payment.SyncEntityType, payment.Id, order.Id,
            before: new { paidAmount = order.PaidAmount.ToString() },
            after: new { paymentId = payment.Id, amount = payment.Amount.ToString(), paymentMethod = method, paidAmount = balance.PaidAmount.ToString() });
        return new RegisterPaymentResultDto(await dtos.BuildAsync(payment, null, cancellationToken), balance);
    }
}

/// POST /payments/{id}/reverse [I][M]: If-Match = versão do pedido. O estorno pode destravar o pedido (ADR-0022).
internal sealed class ReversePaymentHandler(
    AppDbContext db,
    ITenantContext tenant,
    IIdGenerator ids,
    IConcurrencyContext concurrency,
    IBusinessDayReader businessDays,
    IOrderPaymentPort orders,
    IAuditWriter audit,
    PaymentDtoBuilder dtos,
    TimeProvider time) : ICommandHandler<ReversePaymentCommand, ReversePaymentResultDto>
{
    public async Task<ReversePaymentResultDto> HandleAsync(ReversePaymentCommand command, CancellationToken cancellationToken)
    {
        var day = await businessDays.GetOpenAsync(cancellationToken) ?? throw new BusinessException(ErrorCodes.BusinessDayRequired);
        var payment = await db.Set<Payment>().SingleOrDefaultAsync(p => p.Id == command.PaymentId, cancellationToken)
            ?? throw new NotFoundException("PAYMENT", command.PaymentId);

        // Pedido cancelado não tem pagamento confirmado (ADR-0022), então o LoadForPayment não recusa um estorno válido.
        if (payment.Status == PaymentStatus.Reversed)
        {
            throw new BusinessException(ErrorCodes.PaymentAlreadyReversed);
        }

        await orders.LoadForPaymentAsync(payment.OrderId, concurrency.ExpectedVersion, cancellationToken);
        var reversal = payment.Reverse(ids.New(), day.Id, day.BusinessDate, command.Request.Reason, tenant.EmployeeId!.Value, time.GetUtcNow());
        db.Add(reversal);
        var balance = await orders.UndoPaymentAsync(payment.OrderId, payment.Amount, cancellationToken);

        audit.Add("PaymentReversed", payment.SyncEntityType, payment.Id, payment.OrderId,
            before: new { status = "CONFIRMED", amount = payment.Amount.ToString() },
            after: new { status = "REVERSED", reason = reversal.Reason, paidAmount = balance.PaidAmount.ToString() });
        return new ReversePaymentResultDto(await dtos.BuildReversalAsync(reversal, cancellationToken), balance);
    }
}

internal sealed class ListOrderPaymentsQueryHandler(AppDbContext db, PaymentDtoBuilder dtos)
    : IQueryHandler<ListOrderPaymentsQuery, ItemsDto<PaymentDto>>
{
    public async Task<ItemsDto<PaymentDto>> HandleAsync(ListOrderPaymentsQuery query, CancellationToken cancellationToken)
    {
        var payments = await db.Set<Payment>().AsNoTracking()
            .Where(p => p.OrderId == query.OrderId)
            .OrderBy(p => p.PaidAt).ThenBy(p => p.Id)
            .ToListAsync(cancellationToken);
        return new ItemsDto<PaymentDto>(await dtos.BuildManyAsync(payments, cancellationToken));
    }
}

internal sealed class GetPaymentQueryHandler(AppDbContext db, PaymentDtoBuilder dtos) : IQueryHandler<GetPaymentQuery, PaymentDto>
{
    public async Task<PaymentDto> HandleAsync(GetPaymentQuery query, CancellationToken cancellationToken)
    {
        var payment = await db.Set<Payment>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == query.PaymentId, cancellationToken)
            ?? throw new NotFoundException("PAYMENT", query.PaymentId);
        return (await dtos.BuildManyAsync([payment], cancellationToken))[0];
    }
}

/// Os `payments` do `Order` (porta declarada no Ordering).
internal sealed class OrderPaymentsReader(AppDbContext db) : IOrderPaymentsReader
{
    public async Task<IReadOnlyList<OrderPaymentDto>> ListAsync(Guid orderId, CancellationToken cancellationToken) =>
        (await db.Set<Payment>().AsNoTracking()
            .Where(p => p.OrderId == orderId)
            .OrderBy(p => p.PaidAt).ThenBy(p => p.Id)
            .ToListAsync(cancellationToken))
        .Select(p => new OrderPaymentDto(
            p.Id, p.Amount, UpperSnakeEnumConverter<PaymentMethod>.ToText(p.PaymentMethod), p.Label,
            UpperSnakeEnumConverter<PaymentStatus>.ToText(p.Status), p.PaidAt, p.BusinessDate))
        .ToList();
}

/// Monta `Payment` e `PaymentReversal` de 06 §6.10 com os autores do Identity.
internal sealed class PaymentDtoBuilder(AppDbContext db, IEmployeeDirectory employees)
{
    public async Task<PaymentDto> BuildAsync(Payment payment, PaymentReversal? reversal, CancellationToken cancellationToken)
    {
        var names = await employees.GetDisplayNamesAsync(
            reversal is null ? [payment.CreatedByEmployeeId] : [payment.CreatedByEmployeeId, reversal.CreatedByEmployeeId], cancellationToken);
        return Build(payment, reversal, names);
    }

    public async Task<IReadOnlyList<PaymentDto>> BuildManyAsync(IReadOnlyList<Payment> payments, CancellationToken cancellationToken)
    {
        var ids = payments.Select(p => p.Id).ToArray();
        var reversals = await db.Set<PaymentReversal>().AsNoTracking()
            .Where(r => ids.Contains(r.PaymentId))
            .ToDictionaryAsync(r => r.PaymentId, cancellationToken);
        var names = await employees.GetDisplayNamesAsync(
            payments.Select(p => p.CreatedByEmployeeId).Concat(reversals.Values.Select(r => r.CreatedByEmployeeId)), cancellationToken);
        return payments.Select(p => Build(p, reversals.GetValueOrDefault(p.Id), names)).ToList();
    }

    public async Task<PaymentReversalDto> BuildReversalAsync(PaymentReversal r, CancellationToken cancellationToken)
    {
        var names = await employees.GetDisplayNamesAsync([r.CreatedByEmployeeId], cancellationToken);
        return new PaymentReversalDto(
            r.Id, r.PaymentId, r.OrderId, r.BusinessDayId, r.BusinessDate, r.Amount, r.PaymentMethod, r.Reason, r.CreatedAt,
            Author(r.CreatedByEmployeeId, names));
    }

    private static PaymentDto Build(Payment p, PaymentReversal? r, IReadOnlyDictionary<Guid, string> names) => new(
        p.Id, p.OrderId, p.BusinessDayId, p.BusinessDate, p.Amount, p.PaymentMethod, p.CashReceived, p.ChangeGiven, p.Label, p.Status, p.PaidAt,
        Author(p.CreatedByEmployeeId, names),
        r is null ? null : new PaymentReversalSummaryDto(r.Id, r.BusinessDate, r.Reason, r.CreatedAt, Author(r.CreatedByEmployeeId, names)));

    private static AuthorDto Author(Guid id, IReadOnlyDictionary<Guid, string> names) => new(id, names.GetValueOrDefault(id, string.Empty));
}

/// Formatos de 05 §5.10. Saldo, método habilitado e troco dependem de estado e ficam no handler/domínio.
internal sealed class RegisterPaymentRequestValidator : AbstractValidator<RegisterPaymentRequest>
{
    public RegisterPaymentRequestValidator()
    {
        RuleFor(x => x.PaymentMethod).IsInEnum();
        RuleFor(x => x.Label).MaximumLength(30);
    }
}

internal sealed class ReversePaymentRequestValidator : AbstractValidator<ReversePaymentRequest>
{
    public ReversePaymentRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Informe o motivo.").MaximumLength(300);
    }
}
