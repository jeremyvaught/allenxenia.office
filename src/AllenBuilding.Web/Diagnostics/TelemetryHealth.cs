using System.Diagnostics.Tracing;
using System.Reflection;
using AllenBuilding.Application.Diagnostics;

namespace AllenBuilding.Web.Diagnostics;

public sealed class TelemetryIdentity(IHostEnvironment environment)
{
    public string Service => "allen-xenia-office";
    public string Environment => environment.EnvironmentName is "Development" or "Testing" or "Production" ? environment.EnvironmentName : "Other";
    public string Release => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
}
// The SDK reports its batch-drop total at shutdown. Ignore every other event and all free-form payloads.
public sealed class TelemetryDropListener(ILogger<TelemetryDropListener> logger) : EventListener
{
    private long _dropped;
    public long Dropped => Interlocked.Read(ref _dropped);
    protected override void OnEventSourceCreated(EventSource eventSource)
    { if (eventSource.Name == "OpenTelemetry-Sdk") EnableEvents(eventSource, EventLevel.Warning); }
    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventName != "ExistsDroppedExportProcessorItems" || eventData.Payload?.Count != 3 || eventData.Payload[2] is not long count || count <= 0) return;
        Interlocked.Add(ref _dropped, count); OfficeTelemetry.TracesDropped(count);
        try { logger.LogWarning("Telemetry batch dropped spans {DroppedCount}", count); }
        catch (Exception) { OfficeTelemetry.ExportFailed("traces"); }
    }
}
