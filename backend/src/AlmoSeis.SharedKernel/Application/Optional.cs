namespace AlmoSeis.SharedKernel.Application;

/// Campo de PATCH: distingue "ausente" (não mexe) de "presente com null" (limpa) (06 §6.0).
/// O conversor JSON fica no Api; propriedade ausente no corpo = `default` = sem valor.
public readonly record struct Optional<T>
{
    public Optional(T value)
    {
        HasValue = true;
        Value = value;
    }

    public bool HasValue { get; }

    public T Value { get; }

    public T GetValueOr(T fallback) => HasValue ? Value : fallback;

    public static implicit operator Optional<T>(T value) => new(value);

    public override string ToString() => HasValue ? $"{Value}" : "(ausente)";
}
