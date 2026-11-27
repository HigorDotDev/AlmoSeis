using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AlmoSeis.Infrastructure.Persistence;

/// Ações que só valem se a transação do `AppDbContext` for confirmada (07 §7.3), registradas por `IUnitOfWork.OnCommitted`.
/// Hoje: contadores de métrica (`almoseis.orders.created`), para um pedido desfeito no rollback não contar. Rodam em
/// memória logo depois do `COMMIT`, sem I/O; uma falha vira aviso no log e não afeta a resposta. Rollback ou falha da
/// transação descartam a lista. Interceptor por escopo, como os de tenant.
public sealed class CommitCallbacks(ILogger<CommitCallbacks> logger) : DbTransactionInterceptor
{
    private readonly List<Action> _pending = [];

    public void Add(Action action) => _pending.Add(action);

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Run();

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Run();
        return Task.CompletedTask;
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => _pending.Clear();

    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        _pending.Clear();
        return Task.CompletedTask;
    }

    public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) => _pending.Clear();

    public override Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        _pending.Clear();
        return Task.CompletedTask;
    }

    private void Run()
    {
        var actions = _pending.ToArray();
        _pending.Clear();
        foreach (var action in actions)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Falha numa ação pós-commit.");
            }
        }
    }
}
