using System.Text.RegularExpressions;
using AlmoSeis.Modules.Notifications.Contracts;
using AlmoSeis.SharedKernel.Domain;

namespace AlmoSeis.Modules.Notifications.Domain;

/// Aparelho que recebe push no estabelecimento (04 §4.15, ADR-0049 item 4). Um por instalação e tenant; o mesmo token
/// do Expo em um registro só (o aparelho que troca de estabelecimento sai do anterior). Os destinatários são resolvidos
/// no envio, pelas permissões atuais do funcionário do registro (item 5).
internal sealed partial class PushRegistration : Entity, ITenantOwned
{
    public const int TokenMax = 200;

    private PushRegistration()
    {
    }

    public Guid TenantId { get; private set; }
    public string InstallationId { get; private set; } = null!;
    public Guid TenantEmployeeId { get; private set; }

    /// Login por PIN: o registro é do aparelho compartilhado, e vale o último funcionário que entrou (item 5).
    public Guid? SharedDeviceId { get; private set; }
    public PushPlatform Platform { get; private set; }
    public string ExpoPushToken { get; private set; } = null!;

    /// "Avisar pedidos do site com o app fechado" (item 6).
    public bool WebOrdersEnabled { get; private set; }

    /// Usuário com vínculo em mais de um estabelecimento: o título leva o nome do estabelecimento (item 3).
    public bool ShowTenantName { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// Último registro feito pelo app: sem uso há 30 dias, o registro é apagado (item 4).
    public DateTimeOffset UpdatedAt { get; private set; }

    public static bool IsExpoToken(string? token) =>
        token is { Length: > 0 and <= TokenMax } && ExpoTokenFormat().IsMatch(token);

    public static PushRegistration Create(
        Guid id, Guid tenantId, string installationId, Guid employeeId, Guid? sharedDeviceId, PushPlatform platform, string token,
        bool webOrdersEnabled, bool showTenantName, DateTimeOffset now) => new()
    {
        Id = id,
        TenantId = tenantId,
        InstallationId = installationId,
        TenantEmployeeId = employeeId,
        SharedDeviceId = sharedDeviceId,
        Platform = platform,
        ExpoPushToken = token,
        WebOrdersEnabled = webOrdersEnabled,
        ShowTenantName = showTenantName,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public void Refresh(Guid employeeId, Guid? sharedDeviceId, PushPlatform platform, string token, bool? webOrdersEnabled, bool showTenantName, DateTimeOffset now)
    {
        TenantEmployeeId = employeeId;
        SharedDeviceId = sharedDeviceId;
        Platform = platform;
        ExpoPushToken = token;
        WebOrdersEnabled = webOrdersEnabled ?? WebOrdersEnabled;
        ShowTenantName = showTenantName;
        UpdatedAt = now;
    }

    /// Para log: nunca o token inteiro (02-padroes, ADR-0052 item 7).
    public static string Mask(string token)
    {
        var open = token.IndexOf('[', StringComparison.Ordinal);
        return open < 0 || token.Length < open + 5 ? "***" : $"{token[..(open + 5)]}…]";
    }

    [GeneratedRegex(@"^Expo(nent)?PushToken\[[A-Za-z0-9_\-]{8,180}\]$")]
    private static partial Regex ExpoTokenFormat();
}

/// Um push enviado (ou recusado) para um registro por mensagem do outbox: deduplica o reenvio do outbox (ADR-0031) e
/// guarda o ticket da Expo para a conferência do recibo depois de 15 min (ADR-0049 item 8). Sem token nem conteúdo.
internal sealed class PushDelivery : ITenantOwned
{
    public const int ErrorMax = 40;

    private PushDelivery()
    {
    }

    public Guid EventId { get; private set; }
    public Guid RegistrationId { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrderId { get; private set; }
    public PushKind Kind { get; private set; }
    public string? TicketId { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset SentAt { get; private set; }
    public DateTimeOffset? ReceiptCheckedAt { get; private set; }
    public string? ReceiptError { get; private set; }

    public static PushDelivery Record(Guid eventId, Guid registrationId, Guid tenantId, Guid orderId, PushKind kind, string? ticketId, string? error, DateTimeOffset now) =>
        new()
        {
            EventId = eventId,
            RegistrationId = registrationId,
            TenantId = tenantId,
            OrderId = orderId,
            Kind = kind,
            TicketId = ticketId,
            Error = ticketId is null ? Truncate(error ?? "Unknown") : null,
            SentAt = now,
        };

    public void CheckReceipt(string? error, DateTimeOffset now)
    {
        ReceiptCheckedAt = now;
        ReceiptError = error is null ? null : Truncate(error);
    }

    private static string Truncate(string error) => error.Length > ErrorMax ? error[..ErrorMax] : error;
}

/// Erros da Expo que importam aqui (tickets e recibos).
internal static class ExpoErrors
{
    /// O aparelho não aceita mais push com esse token (app desinstalado, permissão retirada): o registro é apagado.
    public const string DeviceNotRegistered = "DeviceNotRegistered";

    /// Recibo que a Expo não devolveu (ela guarda por 24 h): conferência encerrada sem resultado.
    public const string ReceiptUnavailable = "ReceiptUnavailable";
}

/// Título e corpo do push do pedido do site (ADR-0049 itens 2 e 3): só o tipo, a quantidade e o modo. Sem nome,
/// telefone, endereço nem valor, porque aparece na tela bloqueada. Pedido de marmita: "2 marmitas"; à la carte (EP-003
/// fatia 5, `marmitaCount` 0): "3 itens", pelo `itemCount` (pratos, combos e bebidas).
internal static class WebOrderPushContent
{
    public static (string Title, string Body) For(
        PushKind kind, int marmitaCount, string fulfillmentType, int minutesLeft, string? tenantName, int itemCount = 0)
    {
        var title = kind == PushKind.WebOrderReminder ? "Pedido do site esperando" : "Novo pedido do site";
        var count = marmitaCount > 0 || itemCount <= 0
            ? $"{marmitaCount} {(marmitaCount == 1 ? "marmita" : "marmitas")}"
            : $"{itemCount} {(itemCount == 1 ? "item" : "itens")}";
        var body = kind == PushKind.WebOrderReminder
            ? $"Faltam {minutesLeft} min para expirar"
            : $"{count} · {Mode(fulfillmentType)} · aceite em até {minutesLeft} min";
        return (tenantName is null ? title : $"{title} · {tenantName}", body);
    }

    /// Minutos inteiros que ainda faltam, arredondados para cima (7 min e 59 s → 8; 7 min → 7).
    public static int MinutesLeft(DateTimeOffset deadline, DateTimeOffset now) =>
        (int)Math.Ceiling(Math.Round((deadline - now).TotalSeconds) / 60);

    private static string Mode(string fulfillmentType) => fulfillmentType switch
    {
        "DELIVERY" => "Entrega",
        "PICKUP" => "Retirada",
        _ => "Pedido",
    };
}
