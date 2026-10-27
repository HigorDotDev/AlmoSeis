using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;

namespace AlmoSeis.Modules.Menu.Contracts;

public enum MenuType
{
    Marmita,
    Pf,
}

public enum MenuKind
{
    Daily,
    WeeklyTemplate,
}

public enum MenuSection
{
    MainDish,
    SideDish,
}

// ---------- DTOs (06 §6.7) ----------

public sealed record MenuItemDto(
    Guid Id,
    Guid ProductId,
    MenuSection Section,
    string Name,
    string? Description,
    string? ImageUrl,
    Money? PfPrice,
    bool IsAvailable,
    int SortOrder);

public sealed record MenuDto(
    Guid Id,
    int Version,
    MenuType MenuType,
    MenuKind Kind,
    DateOnly? Date,
    int? DayOfWeek,
    Guid? SourceMenuId,
    IReadOnlyList<MenuItemDto> Items,
    DateTimeOffset CreatedAt,
    AuthorDto? CreatedBy,
    DateTimeOffset UpdatedAt) : IVersionedDto;

/// `Menu` sem `items`, com `itemCount`.
public sealed record MenuSummaryDto(
    Guid Id,
    int Version,
    MenuType MenuType,
    MenuKind Kind,
    DateOnly? Date,
    int? DayOfWeek,
    Guid? SourceMenuId,
    int ItemCount,
    DateTimeOffset CreatedAt,
    AuthorDto? CreatedBy,
    DateTimeOffset UpdatedAt) : IVersionedDto;

// ---------- Comandos e consultas ----------

public sealed record MenuItemInput(Guid ProductId, Money? PfPrice, bool IsAvailable = true, int SortOrder = 0);

public sealed record CreateMenuCommand(MenuType MenuType, MenuKind Kind, DateOnly? Date, int? DayOfWeek, IReadOnlyList<MenuItemInput> Items);

public sealed record ReplaceMenuItemsRequest(IReadOnlyList<MenuItemInput> Items);

public sealed record ReplaceMenuItemsCommand(Guid MenuId, ReplaceMenuItemsRequest Request);

public sealed record UpdateMenuItemRequest(Optional<bool> IsAvailable, Optional<Money?> PfPrice);

public sealed record UpdateMenuItemCommand(Guid MenuId, Guid ItemId, UpdateMenuItemRequest Changes);

public sealed record CloneMenuRequest(MenuKind Kind, DateOnly? Date, int? DayOfWeek);

public sealed record CloneMenuCommand(Guid MenuId, CloneMenuRequest Request);

public sealed record GetMenuQuery(Guid MenuId);

public sealed record ListMenusQuery(MenuType? MenuType, MenuKind? Kind, DateOnly? From, DateOnly? To);

public sealed record GetCurrentMenuQuery(MenuType MenuType);

// ---------- Portas (07 §7.7) ----------

/// Data do expediente aberto. Declarada aqui e implementada pelo Closing, que é dono do expediente.
public interface IOpenBusinessDate
{
    Task<DateOnly?> GetAsync(CancellationToken cancellationToken);
}

/// Cardápio criado pela abertura do expediente a partir do modelo semanal.
public sealed record MaterializedMenuDto(Guid Id, MenuType MenuType, Guid SourceMenuId);

/// ADR-0038: na abertura, cada WEEKLY_TEMPLATE do dia da semana sem DAILY correspondente vira DAILY.
/// Roda na transação de abertura do expediente.
public interface IMenuMaterializer
{
    Task<IReadOnlyList<MaterializedMenuDto>> MaterializeForAsync(DateOnly businessDate, CancellationToken cancellationToken);
}
