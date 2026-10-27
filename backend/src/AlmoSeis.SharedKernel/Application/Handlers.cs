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

/// Lê o recurso como no GET, para o `current` do 409 VersionConflict (06 §6.1, 07 §7.4).
/// Cada módulo registra um por `entityType` versionado.
public interface ICurrentResourceReader
{
    string EntityType { get; }

    Task<IVersionedDto?> ReadAsync(Guid entityId, CancellationToken cancellationToken);
}

/// Resposta de listagem sem paginação: `{ "items": [...] }` (06 §6.0).
public sealed record ItemsDto<T>(IReadOnlyList<T> Items);

/// Listagem paginada: `{ "items": [...], "nextCursor": "..." | null }` (06 §6.0).
public sealed record PageDto<T>(IReadOnlyList<T> Items, string? NextCursor);

/// Autor de qualquer registro: `{ "id": "<employeeId>", "displayName": "Ana" }` (06 §6.0).
public sealed record AuthorDto(Guid Id, string DisplayName);

public interface IIdGenerator
{
    Guid New();
}

/// UUID v7 ordenável por tempo (04 §4.0) e **monotônico** no processo: dentro do mesmo milissegundo, os 12 bits
/// de `rand_a` viram contador (RFC 9562 §6.2, método 1). `Guid.CreateVersion7` sorteia esses bits, e a ordem por id
/// (itens de um pedido, cursores) deixaria de ser a ordem de criação.
public sealed class IdGenerator(TimeProvider timeProvider) : IIdGenerator
{
    private const int MaxCounter = 0xFFF;
    private readonly Lock _gate = new();
    private long _lastMs = -1;
    private int _counter;

    public Guid New()
    {
        long ms;
        int counter;
        lock (_gate)
        {
            ms = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            if (ms > _lastMs)
            {
                _lastMs = ms;
                _counter = System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 0x800);
            }
            else if (++_counter > MaxCounter)
            {
                // Contador esgotado (ou relógio voltou): avança o milissegundo lógico.
                _lastMs++;
                _counter = 0;
            }

            (ms, counter) = (_lastMs, _counter);
        }

        Span<byte> bytes = stackalloc byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes[8..]);
        bytes[0] = (byte)(ms >> 40);
        bytes[1] = (byte)(ms >> 32);
        bytes[2] = (byte)(ms >> 24);
        bytes[3] = (byte)(ms >> 16);
        bytes[4] = (byte)(ms >> 8);
        bytes[5] = (byte)ms;
        bytes[6] = (byte)(0x70 | (counter >> 8));
        bytes[7] = (byte)counter;
        bytes[8] = (byte)(0x80 | (bytes[8] & 0x3F));
        return new Guid(bytes, bigEndian: true);
    }
}

/// Resposta de escrita idempotente: o id gravado em `resource_id`, devolvido por GET /operations/{key} (06 §6.12).
/// Implementado de forma explícita para não aparecer no JSON.
public interface IHasResourceId
{
    Guid ResourceId { get; }
}
