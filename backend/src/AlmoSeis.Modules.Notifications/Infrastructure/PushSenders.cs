using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlmoSeis.Modules.Notifications.Application;
using AlmoSeis.Modules.Notifications.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Modules.Notifications.Infrastructure;

/// API de push da Expo (ADR-0049 item 1): `POST /push/send` com até 100 mensagens e `POST /push/getReceipts` com até
/// 1000 tickets. `Authorization: Bearer` só com `Push:Expo:AccessToken`. O cliente HTTP não registra log de requisição
/// (o corpo leva os tokens), e as mensagens de erro nunca levam token.
internal sealed class ExpoPushSender(HttpClient http, IOptions<PushOptions> options) : IPushSender
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<IReadOnlyList<PushTicket>> SendAsync(IReadOnlyList<PushMessage> messages, CancellationToken cancellationToken)
    {
        var body = messages.Select(m => new ExpoMessage(m.To, m.Title, m.Body, m.Data, m.Sound, m.ChannelId, "high", m.TtlSeconds)).ToList();
        using var response = await PostAsync(options.Value.Expo.SendUrl, body, cancellationToken);
        var result = await ReadAsync<ExpoSendResponse>(response, cancellationToken);
        if (result.Data is not { } tickets)
        {
            throw new PushUnavailableException($"Expo recusou o lote: {Describe(result.Errors)}.");
        }

        return tickets
            .Select(t => t.Status == "ok" && t.Id is { Length: > 0 }
                ? new PushTicket(t.Id, null)
                : new PushTicket(null, t.Details?.Error ?? "Unknown"))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<string, PushReceipt>> GetReceiptsAsync(IReadOnlyList<string> ticketIds, CancellationToken cancellationToken)
    {
        using var response = await PostAsync(options.Value.Expo.ReceiptsUrl, new { ids = ticketIds }, cancellationToken);
        var result = await ReadAsync<ExpoReceiptsResponse>(response, cancellationToken);
        if (result.Data is not { } receipts)
        {
            throw new PushUnavailableException($"Expo recusou a consulta de recibos: {Describe(result.Errors)}.");
        }

        return receipts.ToDictionary(
            r => r.Key,
            r => new PushReceipt(r.Value.Status == "ok" ? null : r.Value.Details?.Error ?? "Unknown"));
    }

    private async Task<HttpResponseMessage> PostAsync<T>(string url, T body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: Json) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (options.Value.Expo.AccessToken is { Length: > 0 } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.Expo.TimeoutSeconds));
        try
        {
            var response = await http.SendAsync(request, timeout.Token);
            if (response.StatusCode is HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                var status = (int)response.StatusCode;
                response.Dispose();
                throw new PushUnavailableException($"Expo respondeu {status}.");
            }

            return response;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PushUnavailableException("Expo não respondeu a tempo.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new PushUnavailableException($"Falha de rede com a Expo ({ex.HttpRequestError}).", ex);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
                ?? throw new PushUnavailableException($"Expo respondeu {(int)response.StatusCode} sem corpo.");
        }
        catch (JsonException ex)
        {
            throw new PushUnavailableException($"Expo respondeu {(int)response.StatusCode} com um corpo inesperado.", ex);
        }
    }

    // Os códigos de erro da Expo (`code`) não levam token; a mensagem (`message`) pode levar, então fica de fora.
    private static string Describe(IReadOnlyList<ExpoRequestError>? errors) =>
        errors is { Count: > 0 } ? string.Join(", ", errors.Select(e => e.Code ?? "?")) : "sem detalhes";

    private sealed record ExpoMessage(
        string To,
        string Title,
        string Body,
        IReadOnlyDictionary<string, string> Data,
        string Sound,
        string ChannelId,
        string Priority,
        int Ttl);

    private sealed record ExpoSendResponse(IReadOnlyList<ExpoTicket>? Data, IReadOnlyList<ExpoRequestError>? Errors);

    private sealed record ExpoTicket(string? Status, string? Id, ExpoErrorDetails? Details);

    private sealed record ExpoReceiptsResponse(IReadOnlyDictionary<string, ExpoTicket>? Data, IReadOnlyList<ExpoRequestError>? Errors);

    private sealed record ExpoErrorDetails(string? Error);

    private sealed record ExpoRequestError(string? Code);
}

/// Desenvolvimento e testes (`Push:Provider = Fake`): não chama a rede, guarda o que "enviou" e registra em log só o
/// título e o token mascarado. Um token com `DeviceNotRegistered` no meio responde como aparelho desinstalado, no ticket;
/// `ReceiptErrors` define recibos com erro (testes).
internal sealed class FakePushSender(ILogger<FakePushSender> logger) : IPushSender
{
    public const string UnregisteredMarker = "DeviceNotRegistered";

    private int _sequence;

    public ConcurrentQueue<PushMessage> Sent { get; } = new();

    public ConcurrentDictionary<string, string> ReceiptErrors { get; } = new();

    private readonly ConcurrentDictionary<string, int> _failures = new();

    /// Expo simulada fora do ar (testes): as próximas `calls` chamadas de envio com esse token falham.
    public void FailFor(string token, int calls) => _failures[token] = calls;

    public Task<IReadOnlyList<PushTicket>> SendAsync(IReadOnlyList<PushMessage> messages, CancellationToken cancellationToken)
    {
        foreach (var message in messages)
        {
            if (_failures.TryGetValue(message.To, out var left) && left > 0)
            {
                _failures[message.To] = left - 1;
                throw new PushUnavailableException("Expo simulada fora do ar.");
            }
        }

        var tickets = new List<PushTicket>();
        foreach (var message in messages)
        {
            if (message.To.Contains(UnregisteredMarker, StringComparison.Ordinal))
            {
                tickets.Add(new PushTicket(null, ExpoErrors.DeviceNotRegistered));
                continue;
            }

            Sent.Enqueue(message);
            logger.LogInformation("Push simulado para {Token}: {Title}.", PushRegistration.Mask(message.To), message.Title);
            tickets.Add(new PushTicket($"fake-{Interlocked.Increment(ref _sequence)}-{Guid.NewGuid():N}", null));
        }

        return Task.FromResult<IReadOnlyList<PushTicket>>(tickets);
    }

    public Task<IReadOnlyDictionary<string, PushReceipt>> GetReceiptsAsync(IReadOnlyList<string> ticketIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, PushReceipt>>(
            ticketIds.ToDictionary(id => id, id => new PushReceipt(ReceiptErrors.TryGetValue(id, out var error) ? error : null)));
}

/// `Push:Provider = None`: nada sai.
internal sealed class NoPushSender : IPushSender
{
    public Task<IReadOnlyList<PushTicket>> SendAsync(IReadOnlyList<PushMessage> messages, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PushTicket>>([]);

    public Task<IReadOnlyDictionary<string, PushReceipt>> GetReceiptsAsync(IReadOnlyList<string> ticketIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, PushReceipt>>(new Dictionary<string, PushReceipt>());
}
