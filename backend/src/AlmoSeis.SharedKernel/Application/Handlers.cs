namespace AlmoSeis.SharedKernel.Application;

/// Caso de uso de escrita. Envolvido pelo decorator transacional (07 §7.3).
public interface ICommandHandler<in TCommand, TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken);
}

/// Caso de uso de leitura: projeção direta para DTO, sem transação de escrita.
public interface IQueryHandler<in TQuery, TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken);
}

/// DTO de recurso versionado: o filtro de ETag escreve `ETag: "{Version}"`.
public interface IVersionedDto
{
    int Version { get; }
}

public interface IIdGenerator
{
    Guid New();
}

/// UUID v7 ordenável por tempo (04 §4.0).
public sealed class IdGenerator(TimeProvider timeProvider) : IIdGenerator
{
    public Guid New() => Guid.CreateVersion7(timeProvider.GetUtcNow());
}
