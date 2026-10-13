using System.Diagnostics;
using AllenBuilding.Application.Diagnostics;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Logging.Console;
using MySql.Data.MySqlClient;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AllenBuilding.Web.Diagnostics;

public static class OfficeObservability
{
    public const int MaxTraceQueue = 256;
    public static void AddOfficeObservability(this WebApplicationBuilder builder)
    {
        var ratio = builder.Configuration.GetValue<double?>("Telemetry:TraceSampleRatio") ?? 0.1;
        if (!double.IsFinite(ratio) || ratio < 0 || ratio > 1) throw new InvalidOperationException("Telemetry:TraceSampleRatio must be between zero and one.");
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options => { options.IncludeScopes = false; options.UseUtcTimestamp = true; options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ"; });
        builder.Services.Configure<ConsoleLoggerOptions>(options => { options.MaxQueueLength = 256; options.QueueFullMode = ConsoleLoggerQueueFullMode.DropWrite; });
        // Framework exception/request/SQL logs may contain private values. Our boundary logs only approved fields.
        builder.Services.PostConfigure<LoggerFilterOptions>(options =>
        {
            options.Rules.Clear();
            options.Rules.Add(new LoggerFilterRule(null, null, null, (_, category, level) =>
                category?.StartsWith("AllenBuilding.", StringComparison.Ordinal) == true && level >= LogLevel.Information));
        });
        builder.Services.AddSingleton<TelemetryIdentity>(); builder.Services.AddSingleton<TelemetryDropListener>();
        builder.Services.AddSingleton<BaseExporter<Activity>, SafeTraceExporter>(); builder.Services.AddSingleton<BaseExporter<Metric>, SafeMetricExporter>();
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.Clear().AddService("allen-xenia-office", serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString()))
            .WithTracing(trace => trace.AddSource(OfficeTelemetry.Name).AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddConnectorNet()
                .SetSampler(new TraceIdRatioBasedSampler(ratio))
                .AddProcessor(services =>
                {
                    _ = services.GetRequiredService<TelemetryDropListener>();
                    return new BatchActivityExportProcessor(services.GetRequiredService<BaseExporter<Activity>>(),
                        maxQueueSize: MaxTraceQueue, scheduledDelayMilliseconds: 1000, exporterTimeoutMilliseconds: 1000, maxExportBatchSize: 32);
                }))
            .WithMetrics(metrics => metrics.AddMeter(OfficeTelemetry.Name)
                .AddView("*", new MetricStreamConfiguration { TagKeys = ["kind", "outcome"], CardinalityLimit = 128 })
                .AddReader(services => new PeriodicExportingMetricReader(services.GetRequiredService<BaseExporter<Metric>>(), exportIntervalMilliseconds: 30000, exportTimeoutMilliseconds: 1000)));
    }
    public static async Task RequestAsync(HttpContext context, RequestDelegate next)
    {
        // A server-created root isolates private work from arbitrary incoming context/baggage.
        using var operation = OfficeTelemetry.Begin(OfficeOperationKind.Http);
        var timer = Stopwatch.StartNew(); var failed = false;
        var descriptor = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
        var route = descriptor is null ? context.GetEndpoint()?.DisplayName == "HTTP: GET /health/live" ? "health.live" : "other" : descriptor.ControllerName + "." + descriptor.ActionName;
        context.Response.Headers["X-Office-TraceId"] = operation.TraceId;
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { operation.Result = OfficeOutcome.Cancelled; failed = true; throw; }
        catch (Exception) { operation.Result = OfficeOutcome.Failure; failed = true; throw; }
        finally
        {
            var status = failed ? operation.Result == OfficeOutcome.Cancelled ? 499 : 500 : context.Response.StatusCode;
            if (!failed) operation.Result = status >= 500 ? OfficeOutcome.Failure : status >= 400 ? OfficeOutcome.Denied : OfficeOutcome.Success;
            try
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AllenBuilding.Http")
                .LogInformation("Request {Route} {Outcome} {TraceId} {SpanId} {StatusCode} {DurationMs}", route, OfficeTelemetry.Outcome(operation.Result), operation.TraceId, operation.SpanId, status, timer.Elapsed.TotalMilliseconds);
            }
            catch (Exception) { OfficeTelemetry.ExportFailed("traces"); }
        }
    }
}
