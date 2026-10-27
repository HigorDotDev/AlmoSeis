using AlmoSeis.Modules.Menu.Contracts;
using FluentValidation;

namespace AlmoSeis.Modules.Menu.Application;

internal static class MenuValidation
{
    public const int MaxItems = 100;

    /// ck_menus_kind (05 §5.6): DAILY tem data; WEEKLY_TEMPLATE tem dia da semana (ISO, 1 = segunda).
    public static void SlotRules<T>(AbstractValidator<T> validator, Func<T, MenuKind> kind, Func<T, DateOnly?> date, Func<T, int?> dayOfWeek)
    {
        validator.RuleFor(x => kind(x)).IsInEnum().OverridePropertyName("kind");
        validator.RuleFor(x => date(x)).NotNull().When(x => kind(x) == MenuKind.Daily)
            .OverridePropertyName("date").WithMessage("Informe a data do cardápio.");
        validator.RuleFor(x => dayOfWeek(x)).Null().When(x => kind(x) == MenuKind.Daily)
            .OverridePropertyName("dayOfWeek").WithMessage("Cardápio do dia não tem dia da semana.");
        validator.RuleFor(x => dayOfWeek(x)).NotNull().InclusiveBetween(1, 7).When(x => kind(x) == MenuKind.WeeklyTemplate)
            .OverridePropertyName("dayOfWeek").WithMessage("Informe o dia da semana (1 = segunda … 7 = domingo).");
        validator.RuleFor(x => date(x)).Null().When(x => kind(x) == MenuKind.WeeklyTemplate)
            .OverridePropertyName("date").WithMessage("Modelo semanal não tem data.");
    }

    public static void ItemRules<T>(this IRuleBuilder<T, IReadOnlyList<MenuItemInput>> rule) =>
        rule.NotNull().WithMessage("Informe os itens (pode ser uma lista vazia).")
            .Must(items => items is null || items.Count <= MaxItems).WithMessage($"No máximo {MaxItems} itens.")
            .ForEach(item => item.ChildRules(i =>
            {
                i.RuleFor(x => x.ProductId).NotEmpty();
                i.RuleFor(x => x.SortOrder).GreaterThanOrEqualTo(0);
            }));
}

internal sealed class CreateMenuCommandValidator : AbstractValidator<CreateMenuCommand>
{
    public CreateMenuCommandValidator()
    {
        RuleFor(x => x.MenuType).IsInEnum();
        MenuValidation.SlotRules(this, x => x.Kind, x => x.Date, x => x.DayOfWeek);
        RuleFor(x => x.Items).ItemRules();
    }
}

internal sealed class ReplaceMenuItemsRequestValidator : AbstractValidator<ReplaceMenuItemsRequest>
{
    public ReplaceMenuItemsRequestValidator() => RuleFor(x => x.Items).ItemRules();
}

internal sealed class CloneMenuRequestValidator : AbstractValidator<CloneMenuRequest>
{
    public CloneMenuRequestValidator() => MenuValidation.SlotRules(this, x => x.Kind, x => x.Date, x => x.DayOfWeek);
}
