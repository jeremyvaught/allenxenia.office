using System.Text.Json;
using AllenBuilding.Application.Notifications;

namespace AllenBuilding.Infrastructure.Notifications;

public sealed class LocalInboxTransport(string directory) : IEmailTransport
{
    public async Task<DeliveryResult> SendAsync(EmailEnvelope envelope, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, $"{envelope.MessageId:D}.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, new JsonSerializerOptions { WriteIndented = true });
        if (File.Exists(path)) return (await File.ReadAllBytesAsync(path, cancellationToken)).SequenceEqual(bytes)
            ? new(DeliveryOutcome.Delivered) : new(DeliveryOutcome.PermanentFailure, "MessageMismatch");
        var temporary = Path.Combine(directory, $".{envelope.MessageId:D}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporary, options))
            {
                await file.WriteAsync(bytes, cancellationToken); await file.FlushAsync(cancellationToken);
            }
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path))
            {
                if (!(await File.ReadAllBytesAsync(path, cancellationToken)).SequenceEqual(bytes)) return new(DeliveryOutcome.PermanentFailure, "MessageMismatch");
            }
            return new(DeliveryOutcome.Delivered);
        }
        finally { File.Delete(temporary); }
    }
}

public sealed class UnconfiguredEmailTransport : IEmailTransport
{
    public Task<DeliveryResult> SendAsync(EmailEnvelope envelope, CancellationToken cancellationToken)
        => Task.FromResult(new DeliveryResult(DeliveryOutcome.Retryable, "TransportUnavailable"));
}
