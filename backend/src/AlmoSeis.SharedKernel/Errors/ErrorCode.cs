namespace AlmoSeis.SharedKernel.Errors;

/// Código de erro da API: espelho exato do catálogo de 06 §6.1.
public sealed record ErrorCode(string Code, int HttpStatus, string Title)
{
    /// Tipo RFC 7807: "urn:almoseis:error:order-already-paid".
    public string TypeUri => "urn:almoseis:error:" + ToKebab(Code);

    private static string ToKebab(string pascal)
    {
        var chars = new List<char>(pascal.Length + 8);
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (char.IsUpper(c) && i > 0)
            {
                chars.Add('-');
            }

            chars.Add(char.ToLowerInvariant(c));
        }

        return new string([.. chars]);
    }
}

public static class ErrorCodes
{
    public static readonly ErrorCode MalformedRequest = new("MalformedRequest", 400, "Requisição malformada.");
    public static readonly ErrorCode Unauthenticated = new("Unauthenticated", 401, "Sessão inválida ou expirada.");
    public static readonly ErrorCode InvalidCredentials = new("InvalidCredentials", 401, "Login ou senha inválidos.");
    public static readonly ErrorCode InvalidRefreshToken = new("InvalidRefreshToken", 401, "Sessão expirada. Entre novamente.");
    public static readonly ErrorCode InvalidDeviceSecret = new("InvalidDeviceSecret", 401, "Aparelho compartilhado não reconhecido.");
    public static readonly ErrorCode SubscriptionInactive = new("SubscriptionInactive", 402, "A assinatura do estabelecimento não está ativa.");
    public static readonly ErrorCode PermissionDenied = new("PermissionDenied", 403, "Você não tem permissão para esta ação.");
    public static readonly ErrorCode NoActiveMembership = new("NoActiveMembership", 403, "Você não tem vínculo ativo com nenhum estabelecimento.");
    public static readonly ErrorCode NotFound = new("NotFound", 404, "Registro não encontrado.");
    public static readonly ErrorCode MenuNotFound = new("MenuNotFound", 404, "Ainda não há cardápio para hoje.");
    public static readonly ErrorCode NoOpenBusinessDay = new("NoOpenBusinessDay", 404, "Não há expediente aberto.");
    public static readonly ErrorCode VersionConflict = new("VersionConflict", 409, "O registro foi alterado por outra pessoa.");
    public static readonly ErrorCode OperationInProgress = new("OperationInProgress", 409, "Esta operação ainda está sendo processada.");
    public static readonly ErrorCode BusinessDayRequired = new("BusinessDayRequired", 409, "É preciso abrir o expediente.");
    public static readonly ErrorCode BusinessDayAlreadyOpen = new("BusinessDayAlreadyOpen", 409, "Já existe um expediente aberto.");
    public static readonly ErrorCode BusinessDayClosedToday = new("BusinessDayClosedToday", 409, "Já houve um expediente fechado hoje.");
    public static readonly ErrorCode OrderAlreadyPaid = new("OrderAlreadyPaid", 409, "O pedido já está quitado.");
    public static readonly ErrorCode OrderCanceled = new("OrderCanceled", 409, "O pedido está cancelado.");
    public static readonly ErrorCode OrderHasPayments = new("OrderHasPayments", 409, "Estorne os pagamentos antes de cancelar.");
    public static readonly ErrorCode PaymentAlreadyReversed = new("PaymentAlreadyReversed", 409, "Este pagamento já foi estornado.");
    public static readonly ErrorCode ClosingAlreadyFinalized = new("ClosingAlreadyFinalized", 409, "O expediente já foi fechado.");
    public static readonly ErrorCode EmailAlreadyRegistered = new("EmailAlreadyRegistered", 409, "Este e-mail já está cadastrado.");
    public static readonly ErrorCode PhoneAlreadyRegistered = new("PhoneAlreadyRegistered", 409, "Este telefone já está cadastrado.");
    public static readonly ErrorCode TrialAlreadyClaimed = new("TrialAlreadyClaimed", 409, "O teste grátis já foi usado.");
    public static readonly ErrorCode LastOwner = new("LastOwner", 409, "O estabelecimento precisa de pelo menos um proprietário ativo.");
    public static readonly ErrorCode InvitationNotPending = new("InvitationNotPending", 409, "Este código não está mais válido.");
    public static readonly ErrorCode MenuAlreadyExists = new("MenuAlreadyExists", 409, "Já existe um cardápio para este dia.");
    public static readonly ErrorCode SyncCursorExpired = new("SyncCursorExpired", 410, "A sincronização precisa recomeçar.");
    public static readonly ErrorCode ValidationFailed = new("ValidationFailed", 422, "Dados inválidos.");
    public static readonly ErrorCode IdempotencyKeyReused = new("IdempotencyKeyReused", 422, "Esta chave de operação já foi usada com outros dados.");
    public static readonly ErrorCode PaymentExceedsBalance = new("PaymentExceedsBalance", 422, "O valor passa do saldo do pedido.");
    public static readonly ErrorCode TotalBelowPaidAmount = new("TotalBelowPaidAmount", 422, "O total ficaria menor que o valor já pago.");
    public static readonly ErrorCode PaymentMethodDisabled = new("PaymentMethodDisabled", 422, "Forma de pagamento desabilitada.");
    public static readonly ErrorCode ProductUnavailable = new("ProductUnavailable", 422, "Produto indisponível.");
    public static readonly ErrorCode KiloPriceNotConfigured = new("KiloPriceNotConfigured", 422, "Configure o preço do quilo.");
    public static readonly ErrorCode JustificationRequired = new("JustificationRequired", 422, "Justifique a diferença de caixa.");
    public static readonly ErrorCode PinLocked = new("PinLocked", 423, "PIN bloqueado temporariamente.");
    public static readonly ErrorCode AppVersionUnsupported = new("AppVersionUnsupported", 426, "Atualize o aplicativo.");
    public static readonly ErrorCode IdempotencyKeyRequired = new("IdempotencyKeyRequired", 428, "Envie o header Idempotency-Key.");
    public static readonly ErrorCode IfMatchRequired = new("IfMatchRequired", 428, "Envie o header If-Match.");
    public static readonly ErrorCode TooManyRequests = new("TooManyRequests", 429, "Muitas tentativas.");

    public static IReadOnlyList<ErrorCode> All { get; } =
    [
        MalformedRequest, Unauthenticated, InvalidCredentials, InvalidRefreshToken, InvalidDeviceSecret,
        SubscriptionInactive, PermissionDenied, NoActiveMembership, NotFound, MenuNotFound, NoOpenBusinessDay,
        VersionConflict, OperationInProgress, BusinessDayRequired, BusinessDayAlreadyOpen, BusinessDayClosedToday,
        OrderAlreadyPaid, OrderCanceled, OrderHasPayments, PaymentAlreadyReversed, ClosingAlreadyFinalized,
        EmailAlreadyRegistered, PhoneAlreadyRegistered, TrialAlreadyClaimed, LastOwner, InvitationNotPending,
        MenuAlreadyExists,
        SyncCursorExpired, ValidationFailed, IdempotencyKeyReused, PaymentExceedsBalance, TotalBelowPaidAmount,
        PaymentMethodDisabled, ProductUnavailable, KiloPriceNotConfigured, JustificationRequired, PinLocked,
        AppVersionUnsupported, IdempotencyKeyRequired, IfMatchRequired, TooManyRequests,
    ];
}
