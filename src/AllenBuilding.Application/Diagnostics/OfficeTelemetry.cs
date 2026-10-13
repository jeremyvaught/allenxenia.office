using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AllenBuilding.Application.Diagnostics;

public enum OfficeOperationKind { Http, MaintenanceSubmit, MaintenanceChange, MaintenanceAging, RentBalances, NotificationDispatch, CollectionDispatch, PaymentEvent, CustomerProvision, CashSource }
public enum OfficeOutcome { Success, Denied, Invalid, Conflict, Failure, Cancelled, Retry, Uncertain, Suppressed, LeaseLost }
public sealed record OriginTrace(string TraceId, string SpanId, Guid? OperationId);

public static class OfficeTelemetry
{
    public const string Name = "AllenBuilding.Office";
    public static readonly ActivitySource Activities = new(Name);
    public static readonly Meter Metrics = new(Name);
    private static readonly Counter<long> Operations = Metrics.CreateCounter<long>("office.operation.count");
    private static readonly Histogram<double> Duration = Metrics.CreateHistogram<double>("office.operation.duration", "s");
    private static readonly Counter<long> Dropped = Metrics.CreateCounter<long>("office.telemetry.dropped_traces");
    private static readonly Counter<long> ExportErrors = Metrics.CreateCounter<long>("office.telemetry.export_failures");
    public static string Kind(OfficeOperationKind kind) => kind switch
    {
        OfficeOperationKind.Http => "http",
        OfficeOperationKind.MaintenanceSubmit => "maintenance.submit",
        OfficeOperationKind.MaintenanceChange => "maintenance.change",
        OfficeOperationKind.MaintenanceAging => "maintenance.aging",
        OfficeOperationKind.RentBalances => "billing.balances",
        OfficeOperationKind.NotificationDispatch => "notification.dispatch",
        OfficeOperationKind.CollectionDispatch => "collection.dispatch",
        OfficeOperationKind.PaymentEvent => "payment.event",
        OfficeOperationKind.CustomerProvision => "customer.provision",
        OfficeOperationKind.CashSource => "payment.cash_source",
        _ => "other"
    };
    public static string Outcome(OfficeOutcome outcome) => outcome.ToString().ToLowerInvariant();
    public static bool IsKind(string? value) => value is "traces" or "metrics" || Enum.GetValues<OfficeOperationKind>().Any(x => Kind(x) == value);
    public static bool IsOutcome(string? value) => Enum.GetValues<OfficeOutcome>().Any(x => Outcome(x) == value);
    public static OfficeOperation Begin(OfficeOperationKind kind, Guid? operationId = null, ActivityContext origin = default, Guid? messageId = null)
        => new(kind, operationId, origin, messageId);
    public static OriginTrace? Capture()
    {
        for (var activity = Activity.Current; activity is not null; activity = activity.Parent)
            if (IsKind(activity.GetTagItem("office.kind") as string) && activity.TraceId != default && activity.SpanId != default)
                return new(activity.TraceId.ToHexString(), activity.SpanId.ToHexString(), Guid.TryParse(activity.GetTagItem("office.operation_id") as string, out var id) && id != Guid.Empty ? id : null);
        return null;
    }
    public static ActivityContext Origin(string? trace, string? span)
    {
        if (trace?.Length != 32 || span?.Length != 16 || !trace.All(Uri.IsHexDigit) || !span.All(Uri.IsHexDigit) || trace.All(x => x == '0') || span.All(x => x == '0')) return default;
        var traceId = ActivityTraceId.CreateFromString(trace.ToLowerInvariant().AsSpan()); var spanId = ActivitySpanId.CreateFromString(span.ToLowerInvariant().AsSpan());
        return traceId == default || spanId == default ? default : new(traceId, spanId, ActivityTraceFlags.None);
    }
    public static void ExportFailed(string kind) => ExportErrors.Add(1, new KeyValuePair<string, object?>("kind", kind == "metrics" ? "metrics" : "traces"));
    public static void TracesDropped(long count) { if (count > 0) Dropped.Add(count); }
    internal static void Record(OfficeOperationKind kind, OfficeOutcome outcome, double seconds)
    {
        var tags = new TagList { { "kind", Kind(kind) }, { "outcome", Outcome(outcome) } };
        Operations.Add(1, tags); Duration.Record(seconds, tags);
    }
}

public sealed class OfficeOperation : IDisposable
{
    private readonly OfficeOperationKind _kind;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly Activity? _previous;
    private readonly Activity _activity;
    private int _disposed;
    public OfficeOutcome Result { get; set; } = OfficeOutcome.Failure;
    public string TraceId => _activity.TraceId.ToHexString();
    public string SpanId => _activity.SpanId.ToHexString();
    internal OfficeOperation(OfficeOperationKind kind, Guid? operationId, ActivityContext origin, Guid? messageId)
    {
        _kind = kind; _previous = Activity.Current;
        var worker = kind is OfficeOperationKind.NotificationDispatch or OfficeOperationKind.CollectionDispatch or OfficeOperationKind.PaymentEvent or OfficeOperationKind.CustomerProvision or OfficeOperationKind.CashSource;
        var root = worker || kind == OfficeOperationKind.Http;
        if (root) Activity.Current = null;
        _activity = OfficeTelemetry.Activities.StartActivity(OfficeTelemetry.Kind(kind), worker ? ActivityKind.Consumer : kind == OfficeOperationKind.Http ? ActivityKind.Server : ActivityKind.Internal,
            parentContext: root ? default : Activity.Current?.Context ?? default,
            links: origin == default ? null : new[] { new ActivityLink(origin) })
            ?? new Activity(OfficeTelemetry.Kind(kind)).SetIdFormat(ActivityIdFormat.W3C).Start();
        _activity.SetTag("office.kind", OfficeTelemetry.Kind(kind));
        if (operationId is { } op && op != Guid.Empty) _activity.SetTag("office.operation_id", op.ToString("D"));
        if (messageId is { } message) _activity.SetTag("office.message_id", message.ToString("D"));
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _activity.SetTag("office.outcome", OfficeTelemetry.Outcome(Result));
        _activity.SetStatus(Result is OfficeOutcome.Failure or OfficeOutcome.Uncertain or OfficeOutcome.LeaseLost ? ActivityStatusCode.Error : ActivityStatusCode.Ok);
        _activity.Dispose(); Activity.Current = _previous;
        OfficeTelemetry.Record(_kind, Result, Stopwatch.GetElapsedTime(_started).TotalSeconds);
    }
}
