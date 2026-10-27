using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;

namespace AlmoSeis.Api.Infrastructure;

/// Convenções de JSON de 06 §6.0 / 07 §7.4.
public static class ApiJson
{
    public static void Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper, allowIntegerValues: false));
        options.Converters.Add(new MoneyJsonConverter());
        options.Converters.Add(new UtcDateTimeOffsetConverter());
        options.Converters.Add(new OptionalJsonConverterFactory());
    }
}

/// Campos de PATCH: ausente = Optional sem valor; `null` explícito = Optional com valor nulo.
public sealed class OptionalJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Optional<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(OptionalJsonConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;

    private sealed class OptionalJsonConverter<T> : JsonConverter<Optional<T>>
    {
        public override bool HandleNull => true;

        public override Optional<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // null em campo não anulável (ex.: "isActive": null) é requisição malformada.
            if (reader.TokenType == JsonTokenType.Null && default(T) is not null && Nullable.GetUnderlyingType(typeof(T)) is null)
            {
                throw new JsonException("Este campo não aceita null.");
            }

            return new Optional<T>(JsonSerializer.Deserialize<T>(ref reader, options)!);
        }

        public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options)
        {
            if (value.HasValue)
            {
                JsonSerializer.Serialize(writer, value.Value, options);
            }
            else
            {
                writer.WriteNullValue();
            }
        }
    }
}

/// Dinheiro como string "35.50"; número JSON é recusado (ADR-0030).
public sealed class MoneyJsonConverter : JsonConverter<Money>
{
    public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !Money.TryParse(reader.GetString(), out var money))
        {
            throw new JsonException("Valores monetários são strings com 2 casas, ex.: \"35.50\".");
        }

        return money;
    }

    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

/// Instantes sempre em UTC com sufixo Z.
public sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
}
