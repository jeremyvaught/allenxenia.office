using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AllenBuilding.Application.Notifications;
using AllenBuilding.Infrastructure.Notifications;
using MailKit.Net.Smtp;
using MimeKit;

namespace AllenBuilding.IntegrationTests;

public sealed class SmtpTransportTests
{
    [Theory]
    [InlineData("Accepted", "StartTls", DeliveryOutcome.Delivered)]
    [InlineData("Accepted", "SslOnConnect", DeliveryOutcome.Delivered)]
    [InlineData("TemporaryRecipient", "StartTls", DeliveryOutcome.Retryable)]
    [InlineData("RejectedRecipient", "StartTls", DeliveryOutcome.PermanentFailure)]
    [InlineData("TemporaryData", "StartTls", DeliveryOutcome.Retryable)]
    [InlineData("RejectedData", "StartTls", DeliveryOutcome.PermanentFailure)]
    [InlineData("AuthenticationRejected", "StartTls", DeliveryOutcome.PermanentFailure)]
    [InlineData("NoStartTls", "StartTls", DeliveryOutcome.PermanentFailure)]
    [InlineData("LostAcknowledgement", "StartTls", DeliveryOutcome.Uncertain)]
    [InlineData("DisconnectBeforeAuth", "StartTls", DeliveryOutcome.Retryable)]
    public async Task Real_tls_smtp_distinguishes_rejection_from_ambiguous_acceptance(string scenario, string security, DeliveryOutcome expected)
    {
        await using var server = new SyntheticSmtpServer(scenario, security);
        var transport = new SmtpEmailTransport(Options(server.Port, security), () => new SmtpClient
        {
            ServerCertificateValidationCallback = (_, certificate, _, errors) => certificate?.GetCertHashString() == server.Certificate.GetCertHashString() &&
                (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0
        });
        var envelope = new EmailEnvelope(Guid.NewGuid(), "recipient@example.test", "Synthetic invitation", "Synthetic token #token=never-a-real-secret");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await transport.SendAsync(envelope, deadline.Token);
        Assert.Equal(expected, result.Outcome);
        await server.WaitAsync();
        Assert.DoesNotContain("private-provider-diagnostic", result.Code ?? "");
        if (scenario is "Accepted" or "LostAcknowledgement" or "TemporaryData" or "RejectedData")
        {
            Assert.True(server.TlsEstablished); Assert.True(server.Authenticated);
            using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(server.Message!));
            var parsed = await MimeMessage.LoadAsync(bytes);
            Assert.Equal($"{envelope.MessageId:N}@example.test", parsed.MessageId);
            Assert.Equal(envelope.Subject, parsed.Subject); Assert.Contains(envelope.Text, parsed.TextBody);
            Assert.Single(parsed.To); Assert.Empty(parsed.Cc); Assert.Empty(parsed.Bcc);
        }
        if (scenario is "NoStartTls" or "AuthenticationRejected" or "DisconnectBeforeAuth") Assert.Null(server.Message);
    }

    [Fact]
    public async Task Default_runtime_rejects_an_untrusted_certificate_and_never_authenticates()
    {
        await using var server = new SyntheticSmtpServer("Accepted", "SslOnConnect");
        var result = await new SmtpEmailTransport(Options(server.Port, "SslOnConnect")).SendAsync(Envelope(), default);
        Assert.Equal(DeliveryOutcome.PermanentFailure, result.Outcome); Assert.Equal("SmtpTlsRejected", result.Code);
        await server.WaitAsync(); Assert.False(server.Authenticated); Assert.Null(server.Message);
    }

    [Fact]
    public async Task Invalid_headers_or_mailboxes_and_pre_send_cancellation_do_not_connect()
    {
        await using var server = new SyntheticSmtpServer("Accepted", "StartTls");
        var transport = new SmtpEmailTransport(Options(server.Port, "StartTls"));
        Assert.Equal(DeliveryOutcome.PermanentFailure, (await transport.SendAsync(Envelope() with { Subject = "Header\r\nBcc: other@example.test" }, default)).Outcome);
        Assert.Equal(DeliveryOutcome.PermanentFailure, (await transport.SendAsync(Envelope() with { Recipient = "one@example.test, two@example.test" }, default)).Outcome);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Equal(DeliveryOutcome.Retryable, (await transport.SendAsync(Envelope(), cancelled.Token)).Outcome);
        Assert.False(server.Authenticated); Assert.Null(server.Message);
    }

    [Fact]
    public async Task Cancellation_after_upload_is_uncertain_and_cannot_claim_safe_retry()
    {
        await using var server = new SyntheticSmtpServer("HoldAcknowledgement", "StartTls");
        var transport = new SmtpEmailTransport(Options(server.Port, "StartTls"), () => new SmtpClient
        { ServerCertificateValidationCallback = (_, certificate, _, _) => certificate?.GetCertHashString() == server.Certificate.GetCertHashString() });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var send = transport.SendAsync(Envelope(), cancellation.Token);
        await server.Uploaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        Assert.Equal(DeliveryOutcome.Uncertain, (await send).Outcome);
        Assert.NotNull(server.Message);
    }

    [Fact]
    public async Task Connection_refusal_is_retryable_before_any_message_is_dispatched()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        Assert.Equal(DeliveryOutcome.Retryable, (await new SmtpEmailTransport(Options(port, "StartTls")).SendAsync(Envelope(), default)).Outcome);
    }

    [Theory]
    [InlineData("None", 587, "sender@example.test", "name", "password")]
    [InlineData("StartTlsWhenAvailable", 587, "sender@example.test", "name", "password")]
    [InlineData("StartTls", 0, "sender@example.test", "name", "password")]
    [InlineData("StartTls", 587, "Sender <sender@example.test>", "name", "password")]
    [InlineData("StartTls", 587, "sender@example.test", "", "password")]
    [InlineData("StartTls", 587, "sender@example.test", "name", "")]
    public void Invalid_configuration_fails_without_disclosing_credentials(string security, int port, string sender, string name, string password)
    {
        var options = new SmtpEmailOptions { Host = "smtp.example.test", Port = port, Security = security, FromAddress = sender, UserName = name, Password = password };
        var error = Assert.Throws<InvalidOperationException>(() => new SmtpEmailTransport(options));
        Assert.DoesNotContain("smtp.example.test", error.Message); Assert.DoesNotContain("sender@example.test", error.Message);
        Assert.DoesNotContain(password == "" ? "private-canary" : password, options.ToString()!);
    }

    private static EmailEnvelope Envelope() => new(Guid.NewGuid(), "recipient@example.test", "Synthetic notice", "Synthetic content");
    private static SmtpEmailOptions Options(int port, string security) => new()
    { Host = "localhost", Port = port, Security = security, FromAddress = "sender@example.test", UserName = "synthetic-user", Password = "synthetic-password" };
}

// Only loopback, disposable certificates and example.test recipients. No external SMTP service.
internal sealed class SyntheticSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(15));
    private readonly Task _run;
    public X509Certificate2 Certificate { get; }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public bool Authenticated { get; private set; }
    public bool TlsEstablished { get; private set; }
    public string? Message { get; private set; }
    public TaskCompletionSource Uploaded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SyntheticSmtpServer(string scenario, string security)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); request.CertificateExtensions.Add(names.Build());
        Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        _listener.Start(); _run = RunAsync(scenario, security);
    }

    public Task WaitAsync() => _run.WaitAsync(TimeSpan.FromSeconds(10));

    private async Task RunAsync(string scenario, string security)
    {
        try
        {
            using var socket = await _listener.AcceptTcpClientAsync(_stop.Token);
            Stream stream = socket.GetStream();
            if (security == "SslOnConnect") stream = await UpgradeAsync(stream);
            var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 localhost Synthetic SMTP");
            while (!_stop.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_stop.Token); if (line is null) break;
                if (line.StartsWith("EHLO ", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync("250-localhost");
                    if (!TlsEstablished && scenario != "NoStartTls") await writer.WriteLineAsync("250-STARTTLS");
                    await writer.WriteLineAsync("250 AUTH PLAIN");
                    if (scenario == "DisconnectBeforeAuth") break;
                }
                else if (line == "STARTTLS")
                {
                    await writer.WriteLineAsync("220 Ready for TLS");
                    reader.Dispose(); writer.Dispose(); stream = await UpgradeAsync(stream);
                    reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
                }
                else if (line.StartsWith("AUTH PLAIN", StringComparison.Ordinal))
                {
                    Assert.True(TlsEstablished);
                    if (scenario == "AuthenticationRejected") await writer.WriteLineAsync("535 private-provider-diagnostic");
                    else { Authenticated = true; await writer.WriteLineAsync("235 Authenticated"); }
                }
                else if (line.StartsWith("MAIL FROM:", StringComparison.Ordinal)) await writer.WriteLineAsync("250 Sender accepted");
                else if (line.StartsWith("RCPT TO:", StringComparison.Ordinal)) await writer.WriteLineAsync(scenario switch
                { "TemporaryRecipient" => "450 private-provider-diagnostic", "RejectedRecipient" => "550 private-provider-diagnostic", _ => "250 Recipient accepted" });
                else if (line == "DATA")
                {
                    await writer.WriteLineAsync("354 Send message");
                    var message = new StringBuilder();
                    while (await reader.ReadLineAsync(_stop.Token) is { } part && part != ".") message.AppendLine(part.StartsWith("..", StringComparison.Ordinal) ? part[1..] : part);
                    Message = message.ToString(); Uploaded.TrySetResult();
                    if (scenario == "HoldAcknowledgement") await Task.Delay(Timeout.InfiniteTimeSpan, _stop.Token);
                    if (scenario == "LostAcknowledgement") break;
                    await writer.WriteLineAsync(scenario switch
                    { "TemporaryData" => "451 private-provider-diagnostic", "RejectedData" => "554 private-provider-diagnostic", _ => "250 Message accepted" });
                }
                else if (line == "RSET") await writer.WriteLineAsync("250 Reset");
                else if (line == "QUIT") { await writer.WriteLineAsync("221 Bye"); break; }
                else throw new InvalidOperationException("Unexpected synthetic SMTP command.");
            }
            reader.Dispose(); writer.Dispose(); await stream.DisposeAsync();
        }
        catch (System.Security.Authentication.AuthenticationException) { }
        catch (IOException) { }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }

    private async Task<Stream> UpgradeAsync(Stream stream)
    {
        var tls = new SslStream(stream);
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = Certificate }, _stop.Token);
        TlsEstablished = true; return tls;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync(); _listener.Stop(); await _run;
        Certificate.Dispose(); _stop.Dispose();
    }
}
