using AlmoSeis.Modules.Menu.Contracts;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;

namespace AlmoSeis.Modules.Closing.Contracts;

public enum BusinessDayStatus
{
    Open,
    Closed,
}

// ---------- DTOs (06 §6.6) ----------

public sealed record BusinessDayDto(
    Guid Id,
    int Version,
    DateOnly BusinessDate,
    BusinessDayStatus Status,
    Money OpeningBalance,
    DateTimeOffset OpenedAt,
    AuthorDto OpenedBy,
    DateTimeOffset? ClosedAt,
    AuthorDto? ClosedBy,
    Guid? DailyClosingId) : IVersionedDto;

public sealed record OpenBusinessDayResultDto(BusinessDayDto BusinessDay, IReadOnlyList<MaterializedMenuDto> MaterializedMenus)
    : IHasResourceId
{
    Guid IHasResourceId.ResourceId => BusinessDay.Id;
}

// ---------- Comandos e consultas ----------

public sealed record OpenBusinessDayCommand(Money OpeningBalance, bool ConfirmSameDate);

public sealed record GetCurrentBusinessDayQuery;

public sealed record GetBusinessDayQuery(Guid BusinessDayId);

public sealed record ListBusinessDaysQuery(DateOnly? From, DateOnly? To, int Limit, string? Cursor);

// ---------- Portas (07 §7.9) ----------

public sealed record BusinessDayRef(Guid Id, DateOnly BusinessDate, int Version);

/// Expediente aberto para as escritas que o exigem (ADR-0019). Lê com `FOR SHARE`: a finalização do
/// fechamento (`FOR UPDATE`) espera as escritas em curso, e vice-versa.
public interface IBusinessDayReader
{
    Task<BusinessDayRef?> GetOpenAsync(CancellationToken cancellationToken);
}
