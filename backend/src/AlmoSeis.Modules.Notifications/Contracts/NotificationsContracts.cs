namespace AlmoSeis.Modules.Notifications.Contracts;

/// Plataforma do registro de push. `WEB` fica reservado para o Web Push (Q-26): o banco aceita, a API ainda recusa.
public enum PushPlatform
{
    Android,
    Ios,
    Web,
}

/// O que gerou o push (`data.kind` da notificação, ADR-0049 item 3).
public enum PushKind
{
    WebOrderAwaitingAcceptance,
    WebOrderReminder,
}

/// PUT /devices/push-token (06 §6.3, ADR-0049 item 4). `WebOrdersEnabled` nulo mantém a opção atual (ligada no primeiro
/// registro, item 6).
public sealed record RegisterPushTokenCommand(string InstallationId, string ExpoPushToken, PushPlatform Platform, bool? WebOrdersEnabled);

/// DELETE /devices/push-token: apaga o registro da instalação do token no estabelecimento da sessão.
public sealed record RemovePushTokenCommand;

/// `receivesWebOrders`: a opção está ligada e o funcionário da sessão tem `orders.accept` agora. O token não volta.
public sealed record PushRegistrationDto(
    string InstallationId,
    PushPlatform Platform,
    bool WebOrdersEnabled,
    bool ReceivesWebOrders,
    DateTimeOffset RegisteredAt,
    DateTimeOffset UpdatedAt);
