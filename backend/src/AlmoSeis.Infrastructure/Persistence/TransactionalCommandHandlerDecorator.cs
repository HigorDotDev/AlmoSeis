using AlmoSeis.SharedKernel.Application;

namespace AlmoSeis.Infrastructure.Persistence;

/// Envolve todo ICommandHandler (07 §7.3): abre a transação se ninguém abriu (o IdempotencyFilter
/// pode ter aberto antes), executa o handler, roda o UnitOfWork e faz commit; em erro, rollback.
internal sealed class TransactionalCommandHandlerDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    AppDbContext db,
    IUnitOfWork unitOfWork) : ICommandHandler<TCommand, TResult>
{
    public async Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken)
    {
        var ownsTransaction = db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction ? await db.Database.BeginTransactionAsync(cancellationToken) : null;

        try
        {
            var result = await inner.HandleAsync(command, cancellationToken);
            await unitOfWork.CommitAsync(cancellationToken);

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return result;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            db.ChangeTracker.Clear();
            throw;
        }
    }
}
