using System.Diagnostics;
using System.Text.Json;
using AllenBuilding.Application.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace AllenBuilding.Web.Diagnostics;

public sealed record TraceLink(string TraceId, string SpanId);
public sealed record SafeTrace(string TraceId, string SpanId, string ParentSpanId, string Kind, string Outcome, double DurationMs,
    string? OperationId, string? MessageId, IReadOnlyList<TraceLink> Links)
{
    public static SafeTrace From(Activity activity)
    {
        // Never serialize the activity, events, status description, baggage or arbitrary tags.
        var kind = activity.GetTagItem("office.kind") as string;
        kind = OfficeTelemetry.IsKind(kind) ? kind : activity.Source.Name switch
        { "Microsoft.AspNetCore" => "http.server", "System.Net.Http" => "http.client", _ => "database" };
        var outcome = activity.GetTagItem("office.outcome") as string;
        outcome = OfficeTelemetry.IsOutcome(outcome) ? outcome : activity.Status == ActivityStatusCode.Error ? "failure" : "success";
        string? Id(string key) => Guid.TryParse(activity.GetTagItem(key) as string, out var id) && id != Guid.Empty ? id.ToString("D") : null;
        return new(activity.TraceId.ToHexString(), activity.SpanId.ToHexString(), activity.ParentSpanId.ToHexString(), kind!, outcome!,
            activity.Duration.TotalMilliseconds, Id("office.operation_id"), Id("office.message_id"),
            activity.Links.Take(1).Select(x => new TraceLink(x.Context.TraceId.ToHexString(), x.Context.SpanId.ToHexString())).ToList());
    }
}
public sealed class SafeTraceExporter(ILogger<SafeTraceExporter> logger, TelemetryIdentity identity) : BaseExporter<Activity>
{
    public override ExportResult Export(in Batch<Activity> batch)
    {
        try
        {
            foreach (var activity in batch) logger.LogInformation("Trace {Service} {Environment} {Release} {Telemetry}", identity.Service, identity.Environment, identity.Release, JsonSerializer.Serialize(SafeTrace.From(activity)));
            return ExportResult.Success;
        }
        catch (Exception) { OfficeTelemetry.ExportFailed("traces"); return ExportResult.Failure; }
    }
}
public sealed class SafeMetricExporter(ILogger<SafeMetricExporter> logger, TelemetryIdentity identity) : BaseExporter<Metric>
{
    public override ExportResult Export(in Batch<Metric> batch)
    {
        try
        {
            foreach (var metric in batch)
            {
                if (metric.Name is not ("office.operation.count" or "office.operation.duration" or "office.telemetry.export_failures" or "office.telemetry.dropped_traces")) continue;
                foreach (ref readonly var point in metric.GetMetricPoints())
                {
                    var tags = new Dictionary<string, string>();
                    foreach (var tag in point.Tags)
                        if (tag.Key == "kind" && OfficeTelemetry.IsKind(tag.Value as string) || tag.Key == "outcome" && OfficeTelemetry.IsOutcome(tag.Value as string)) tags[tag.Key] = (string)tag.Value!;
                    var histogram = metric.MetricType == MetricType.Histogram;
                    logger.LogInformation("Metric {Service} {Environment} {Release} {Telemetry}", identity.Service, identity.Environment, identity.Release, JsonSerializer.Serialize(new
                    { Name = metric.Name, Tags = tags, Count = histogram ? point.GetHistogramCount() : point.GetSumLong(), SumSeconds = histogram ? point.GetHistogramSum() : (double?)null }));
                }
            }
            return ExportResult.Success;
        }
        catch (Exception) { OfficeTelemetry.ExportFailed("metrics"); return ExportResult.Failure; }
    }
}
