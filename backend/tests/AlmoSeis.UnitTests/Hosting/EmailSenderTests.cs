using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AlmoSeis.Infrastructure;
using AlmoSeis.Infrastructure.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace AlmoSeis.UnitTests.Hosting;

/// ADR-0060: `Email:Provider` Smtp (MailKit) contra um servidor SMTP falso em memória (loopback), validação da seção e
/// escolha do provedor no DI.
public sealed class EmailSenderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Smtp_sender_authenticates_and_delivers_a_plain_text_message()
    {
        await using var server = FakeSmtpServer.Start();
        var sender = Sender(new SmtpEmailOptions { Host = "127.0.0.1", Port = server.Port, Username = "smtp-user", Password = "smtp-pass", UseStartTls = false });

        await sender.SendAsync("ana@example.com", "Redefinição de senha do AlmoSeis", "Abra o link:\nhttps://app/reset?t=abc", Ct);

        var session = await server.NextSessionAsync();
        session.Auth.ShouldBe("\0smtp-user\0smtp-pass");
        session.MailFrom.ShouldBe("nao-responda@almoseis.app");
        session.Recipients.ShouldBe(["ana@example.com"]);
        session.Data.ShouldContain("From: AlmoSeis <nao-responda@almoseis.app>");
        session.Data.ShouldContain("To: ana@example.com");
        session.Data.ShouldContain("Content-Type: text/plain");
        session.Quit.ShouldBeTrue();
    }

    [Fact]
    public async Task Smtp_without_credentials_skips_auth()
    {
        await using var server = FakeSmtpServer.Start();
        await Sender(new SmtpEmailOptions { Host = "127.0.0.1", Port = server.Port, UseStartTls = false }).SendAsync("ana@example.com", "Assunto", "Corpo", Ct);

        var session = await server.NextSessionAsync();
        session.Auth.ShouldBeNull();
        session.Recipients.ShouldBe(["ana@example.com"]);
    }

    [Fact]
    public async Task Required_starttls_never_falls_back_to_plain_text()
    {
        await using var server = FakeSmtpServer.Start();
        var sender = Sender(new SmtpEmailOptions { Host = "127.0.0.1", Port = server.Port, Username = "u", Password = "p", UseStartTls = true });

        await Should.ThrowAsync<EmailDeliveryException>(() => sender.SendAsync("ana@example.com", "Assunto", "token=segredo", Ct));

        var session = await server.NextSessionAsync();
        session.Auth.ShouldBeNull("a senha não pode ir sem TLS");
        session.Recipients.ShouldBeEmpty();
    }

    [Fact]
    public async Task Rejected_recipient_becomes_a_delivery_error_without_the_address()
    {
        await using var server = FakeSmtpServer.Start(rejectRecipients: true);
        var sender = Sender(new SmtpEmailOptions { Host = "127.0.0.1", Port = server.Port, UseStartTls = false });

        var error = await Should.ThrowAsync<EmailDeliveryException>(() => sender.SendAsync("ana@example.com", "Assunto", "Corpo", Ct));

        error.Message.ShouldNotContain("ana@example.com");
        error.InnerException.ShouldBeNull();
    }

    [Fact]
    public async Task Unreachable_server_becomes_a_delivery_error()
    {
        var port = FakeSmtpServer.FreePort();
        var sender = Sender(new SmtpEmailOptions { Host = "127.0.0.1", Port = port, UseStartTls = false, TimeoutSeconds = 2 });

        await Should.ThrowAsync<EmailDeliveryException>(() => sender.SendAsync("ana@example.com", "Assunto", "Corpo", Ct));
    }

    [Theory]
    [InlineData(true, 587, SmtpSecurity.StartTls)]
    [InlineData(false, 465, SmtpSecurity.ImplicitTls)]
    [InlineData(false, 25, SmtpSecurity.None)]
    public void Tls_mode_follows_use_starttls_and_port(bool startTls, int port, SmtpSecurity expected) =>
        new SmtpEmailOptions { UseStartTls = startTls, Port = port }.Security.ShouldBe(expected);

    [Fact]
    public void Production_requires_a_real_provider_complete_settings_and_tls()
    {
        var production = new EmailOptionsValidator(new Env("Production"));
        production.Validate(null, new EmailOptions()).Failed.ShouldBeTrue("None");
        production.Validate(null, new EmailOptions { Provider = EmailProvider.Fake }).Failed.ShouldBeTrue("Fake");
        production.Validate(null, Smtp(s => s.Host = "")).Failed.ShouldBeTrue("sem host");
        production.Validate(null, Smtp(_ => { }, from: "não é e-mail")).Failed.ShouldBeTrue("remetente inválido");
        production.Validate(null, Smtp(s => s.Password = "")).Failed.ShouldBeTrue("usuário sem senha");
        production.Validate(null, Smtp(s => { s.UseStartTls = false; s.Port = 25; })).Failed.ShouldBeTrue("sem TLS");
        production.Validate(null, new EmailOptions { Provider = EmailProvider.Acs, From = "nao-responda@almoseis.app" }).Failed.ShouldBeTrue("ACS sem connection string");

        production.Validate(null, Smtp(_ => { })).Succeeded.ShouldBeTrue();
        production.Validate(null, Smtp(s => { s.UseStartTls = false; s.Port = 465; })).Succeeded.ShouldBeTrue("TLS implícito");
        production.Validate(null, Smtp(_ => { }, from: "nao-responda@almoseis.app")).Succeeded.ShouldBeTrue("endereço sem nome");
        production.Validate(null, new EmailOptions
        {
            Provider = EmailProvider.Acs,
            From = "nao-responda@almoseis.app",
            Acs = new AcsEmailOptions { ConnectionString = "endpoint=https://x.communication.azure.com/;accesskey=abc" },
        }).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("Staging")]
    public void Outside_production_none_and_plain_smtp_are_allowed(string environment)
    {
        var validator = new EmailOptionsValidator(new Env(environment));
        validator.Validate(null, new EmailOptions()).Succeeded.ShouldBeTrue();
        validator.Validate(null, Smtp(s => { s.UseStartTls = false; s.Port = 25; s.Username = ""; s.Password = ""; })).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null, typeof(LoggingEmailSender))]
    [InlineData("None", typeof(LoggingEmailSender))]
    [InlineData("Fake", typeof(LoggingEmailSender))]
    [InlineData("Smtp", typeof(SmtpEmailSender))]
    [InlineData("Acs", typeof(AcsEmailSender))]
    public void Provider_is_chosen_from_configuration(string? provider, Type expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:Provider"] = provider,
            ["Email:From"] = "nao-responda@almoseis.app",
            ["Email:Smtp:Host"] = "smtp.example.com",
            ["Email:Acs:ConnectionString"] = "endpoint=https://x.communication.azure.com/;accesskey=YWJj",
        }).Build();
        var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(configuration).AddSingleton<IHostEnvironment>(new Env("Testing"));
        services.AddAlmoSeisInfrastructure(configuration);
        using var provider_ = services.BuildServiceProvider();

        provider_.GetRequiredService<IEmailSender>().ShouldBeOfType(expected);
    }

    private static SmtpEmailSender Sender(SmtpEmailOptions smtp) =>
        new(Options.Create(new EmailOptions { Provider = EmailProvider.Smtp, From = "AlmoSeis <nao-responda@almoseis.app>", Smtp = smtp }),
            NullLogger<SmtpEmailSender>.Instance);

    private static EmailOptions Smtp(Action<SmtpEmailOptions> change, string from = "AlmoSeis <nao-responda@almoseis.app>")
    {
        var smtp = new SmtpEmailOptions { Host = "smtp.example.com", Port = 587, Username = "user", Password = "pass", UseStartTls = true };
        change(smtp);
        return new EmailOptions { Provider = EmailProvider.Smtp, From = from, Smtp = smtp };
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "AlmoSeis.Api";

        public string ContentRootPath { get; set; } = ".";

        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}

/// Servidor SMTP mínimo (RFC 5321) em loopback: EHLO sem STARTTLS, AUTH PLAIN/LOGIN, MAIL, RCPT, DATA, QUIT. Guarda o que
/// cada sessão recebeu.
internal sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly bool _rejectRecipients;
    private readonly ConcurrentQueue<Session> _sessions = new();
    private readonly SemaphoreSlim _completed = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    private FakeSmtpServer(bool rejectRecipients)
    {
        _rejectRecipients = rejectRecipients;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static FakeSmtpServer Start(bool rejectRecipients = false) => new(rejectRecipients);

    public static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public async Task<Session> NextSessionAsync()
    {
        (await _completed.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue("a sessão SMTP não terminou");
        _sessions.TryDequeue(out var session).ShouldBeTrue();
        return session!;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        _stop.Dispose();
        _completed.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        var session = new Session();
        try
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                using var reader = new StreamReader(stream, Encoding.ASCII);
                await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("220 fake.smtp ESMTP");
                while (await reader.ReadLineAsync() is { } line)
                {
                    var verb = line.Split(' ', 2)[0].ToUpperInvariant();
                    switch (verb)
                    {
                        case "EHLO":
                        case "HELO":
                            await writer.WriteLineAsync("250-fake.smtp");
                            await writer.WriteLineAsync("250-AUTH PLAIN LOGIN");
                            await writer.WriteLineAsync("250 8BITMIME");
                            break;
                        case "AUTH":
                            session.Auth = await ReadAuthAsync(line, reader, writer);
                            await writer.WriteLineAsync("235 2.7.0 Authentication successful");
                            break;
                        case "MAIL":
                            session.MailFrom = Address(line);
                            await writer.WriteLineAsync("250 2.1.0 Ok");
                            break;
                        case "RCPT":
                            if (_rejectRecipients)
                            {
                                await writer.WriteLineAsync($"550 5.1.1 <{Address(line)}>: Recipient address rejected");
                            }
                            else
                            {
                                session.Recipients.Add(Address(line));
                                await writer.WriteLineAsync("250 2.1.5 Ok");
                            }

                            break;
                        case "DATA":
                            await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                            var data = new StringBuilder();
                            while (await reader.ReadLineAsync() is { } dataLine && dataLine != ".")
                            {
                                data.AppendLine(dataLine);
                            }

                            session.Data = data.ToString();
                            await writer.WriteLineAsync("250 2.0.0 Ok: queued");
                            break;
                        case "QUIT":
                            session.Quit = true;
                            await writer.WriteLineAsync("221 2.0.0 Bye");
                            return;
                        case "STARTTLS":
                            await writer.WriteLineAsync("502 5.5.1 Not implemented");
                            break;
                        default:
                            await writer.WriteLineAsync("250 2.0.0 Ok");
                            break;
                    }
                }
            }
        }
        catch (IOException)
        {
            // O cliente desistiu (ex.: STARTTLS obrigatório que o servidor não oferece).
        }
        finally
        {
            _sessions.Enqueue(session);
            _completed.Release();
        }
    }

    private static async Task<string> ReadAuthAsync(string line, StreamReader reader, StreamWriter writer)
    {
        var parts = line.Split(' ');
        if (parts[1].Equals("PLAIN", StringComparison.OrdinalIgnoreCase))
        {
            var payload = parts.Length > 2 ? parts[2] : await Prompt(reader, writer, "334 ");
            return Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        }

        var user = parts.Length > 2 ? parts[2] : await Prompt(reader, writer, "334 VXNlcm5hbWU6");
        var password = await Prompt(reader, writer, "334 UGFzc3dvcmQ6");
        return "\0" + Encoding.UTF8.GetString(Convert.FromBase64String(user)) + "\0" + Encoding.UTF8.GetString(Convert.FromBase64String(password));
    }

    private static async Task<string> Prompt(StreamReader reader, StreamWriter writer, string challenge)
    {
        await writer.WriteLineAsync(challenge);
        return await reader.ReadLineAsync() ?? string.Empty;
    }

    private static string Address(string line)
    {
        var start = line.IndexOf('<', StringComparison.Ordinal);
        var end = line.IndexOf('>', StringComparison.Ordinal);
        return start >= 0 && end > start ? line[(start + 1)..end] : string.Empty;
    }

    public sealed class Session
    {
        public string? Auth { get; set; }

        public string? MailFrom { get; set; }

        public List<string> Recipients { get; } = [];

        public string Data { get; set; } = string.Empty;

        public bool Quit { get; set; }
    }
}
