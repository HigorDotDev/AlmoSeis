namespace AlmoSeis.Infrastructure.Concurrency;

/// Versão esperada pelo cliente (`If-Match: "{version}"`), por requisição (ADR-0034).
/// Preenchida pelo filtro [RequiresIfMatch]; o handler confere com `aggregate.EnsureVersion(...)`.
public interface IConcurrencyContext
{
    int ExpectedVersion { get; }

    void SetExpectedVersion(int version);
}

internal sealed class ConcurrencyContext : IConcurrencyContext
{
    private int? _expectedVersion;

    public int ExpectedVersion =>
        _expectedVersion ?? throw new InvalidOperationException("If-Match não lido: marque a ação com [RequiresIfMatch].");

    public void SetExpectedVersion(int version) => _expectedVersion = version;
}
