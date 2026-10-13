using System.Net.Sockets;
using AllenBuilding.Application.Notifications;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace AllenBuilding.Infrastructure.Notifications;

// Credentials have no generated ToString implementation and are never logged.
public sealed class SmtpEmailOptions
{
    public string Host { get; init; } = "";
    public int Port { get; init; } = 587;
    public string Security { get; init; } = "StartTls";
    public string UserName { get; init; } = "";
    public string Password { get; init; } = "";
    public string FromAddress { get; init; } = "";
    public string FromName { get; init; } = "Allen Xenia Office";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Host.Length > 253 || Host.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '/' or '\\' or ':' or '@') ||
            Port is < 1 or > 65535 || Security is not ("StartTls" or "SslOnConnect") ||
            string.IsNullOrWhiteSpace(UserName) || UserName.Length > 512 || UserName.Any(char.IsControl) ||
            string.IsNullOrEmpty(Password) || Password.Length > 4096 ||
            !ValidMailbox(FromAddress) || FromName.Length > 120 || FromName.Any(char.IsControl))
            throw new InvalidOperationException("SMTP requires a valid host, port, required TLS, credentials and one sender mailbox.");
    }

    internal static bool ValidMailbox(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 254 && !value.Any(char.IsControl) &&
        MailboxAddress.TryParse(value, out var mailbox) && mailbox.Address == value && string.IsNullOrEmpty(mailbox.Name) && value.Contains('@');
}

public sealed class SmtpEmailTransport : IEmailTransport
{
    private readonly SmtpEmailOptions _options;
    private readonly Func<SmtpClient> _clients;

    public SmtpEmailTransport(SmtpEmailOptions options) : this(options, () => new SmtpClient()) { }

    // Test-only composition seam permits an explicitly pinned synthetic TLS certificate.
    // Runtime composition always uses MailKit's default hostname/chain verification.
    internal SmtpEmailTransport(SmtpEmailOptions options, Func<SmtpClient> clients)
    {
        options.Validate(); _options = options; _clients = clients;
    }

    public async Task<DeliveryResult> SendAsync(EmailEnvelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.MessageId == Guid.Empty || !SmtpEmailOptions.ValidMailbox(envelope.Recipient) ||
            string.IsNullOrWhiteSpace(envelope.Subject) || envelope.Subject.Length > 200 || envelope.Subject.Any(char.IsControl) ||
            string.IsNullOrEmpty(envelope.Text) || envelope.Text.Length > 200_000)
            return new(DeliveryOutcome.PermanentFailure, "InvalidEnvelope");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        var sending = false;
        using var client = _clients();
        client.Timeout = 15_000;
        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
            message.To.Add(new MailboxAddress("", envelope.Recipient));
            message.Subject = envelope.Subject;
            message.MessageId = $"{envelope.MessageId:N}@{_options.FromAddress.Split('@')[1]}";
            message.Body = new TextPart("plain") { Text = envelope.Text };
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
            {
                connect.CancelAfter(TimeSpan.FromSeconds(10));
                await client.ConnectAsync(_options.Host, _options.Port,
                    _options.Security == "StartTls" ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect, connect.Token);
            }
            await client.AuthenticateAsync(_options.UserName, _options.Password, deadline.Token);
            sending = true;
            await client.SendAsync(message, deadline.Token);
            // A successful SendAsync means SMTP acceptance, not delivery to a person's inbox.
            return new(DeliveryOutcome.Delivered, "SmtpAccepted");
        }
        catch (SmtpCommandException error) when ((int)error.StatusCode is >= 400 and < 600)
        {
            // An explicit negative SMTP response proves rejection, so this is safe to retry on 4xx.
            return new((int)error.StatusCode < 500 ? DeliveryOutcome.Retryable : DeliveryOutcome.PermanentFailure, "SmtpRejected");
        }
        catch (MailKit.Security.AuthenticationException) { return new(DeliveryOutcome.PermanentFailure, "SmtpAuthenticationRejected"); }
        catch (SslHandshakeException) { return new(DeliveryOutcome.PermanentFailure, "SmtpTlsRejected"); }
        catch (NotSupportedException) { return new(DeliveryOutcome.PermanentFailure, "SmtpCapabilityMissing"); }
        catch (Exception error) when (error is IOException or SocketException or SmtpProtocolException or OperationCanceledException or ServiceNotConnectedException)
        {
            return new(sending ? DeliveryOutcome.Uncertain : DeliveryOutcome.Retryable, sending ? "SmtpAcceptanceUnknown" : "SmtpUnavailable");
        }
        finally
        {
            // No QUIT round trip can downgrade an already received acceptance.
            if (client.IsConnected) try { await client.DisconnectAsync(quit: false, CancellationToken.None); } catch (Exception) { }
        }
    }
}
