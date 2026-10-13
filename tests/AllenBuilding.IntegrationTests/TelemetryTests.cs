using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AllenBuilding.Application.Diagnostics;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using AllenBuilding.Web.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace AllenBuilding.IntegrationTests;

public sealed class TelemetryTests
{
    [Fact]
    public async Task Tenant_command_correlates_to_outbox_and_redacted_logs_without_trusting_incoming_context()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); var (_, building) = await host.SeedManagerAsync();
        Guid membershipId;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); var manager = await db.Users.SingleAsync();
            var suite = new Suite { BuildingId = building, Name = "Trace test office" }; var membership = Membership.Grant(suite.Id, manager.Id, host.Clock.GetUtcNow().AddDays(-1)); membershipId = membership.Id;
            db.AddRange(suite, membership, new ActiveGrant { UserId = manager.Id, SuiteId = suite.Id, MembershipId = membership.Id }); await db.SaveChangesAsync();
        }
        var logs = new MemoryLogs();
        await using var instrumented = host.WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            { ["Telemetry:TraceSampleRatio"] = "1", ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace", ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "Trace" }));
        });
        using var browser = instrumented.CreateClient(new() { AllowAutoRedirect = false });
        string managerId; using (var scope = host.Services.CreateScope()) managerId = (await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().Users.SingleAsync()).Id;
        await host.LoginManagerAsync(browser, managerId);
        const string clientTrace = "11111111111111111111111111111111";
        browser.DefaultRequestHeaders.Add("traceparent", $"00-{clientTrace}-2222222222222222-01");
        browser.DefaultRequestHeaders.Add("baggage", "secret=private-header-canary");
        var operation = Guid.NewGuid();
        using var response = await AccountTestHost.PostAsync(browser, "/Maintenance/Create", "/Maintenance/Create",
            ("OperationId", operation.ToString()), ("MembershipId", membershipId.ToString()), ("Title", "private-title-canary"), ("Description", "private-body-canary"), ("Location", "Office"));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode); var traceId = response.Headers.GetValues("X-Office-TraceId").Single(); Assert.NotEqual(clientTrace, traceId);
        using (var scope = host.Services.CreateScope())
        {
            var messages = await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().AccountMessages.Where(x => x.OriginOperationId == operation).ToListAsync();
            Assert.NotEmpty(messages); Assert.All(messages, x => { Assert.Equal(traceId, x.OriginTraceId); Assert.Equal(16, x.OriginSpanId?.Length); });
        }
        using var privateQuery = await browser.GetAsync("/Home/Privacy?token=private-query-canary");
        Assert.True(instrumented.Services.GetRequiredService<TracerProvider>().ForceFlush(3000));
        Assert.True(instrumented.Services.GetRequiredService<MeterProvider>().ForceFlush(3000));
        var encoded = string.Join("\n", logs.Items);
        foreach (var secret in new[] { "private-header-canary", "private-title-canary", "private-body-canary", "private-query-canary", "manager@example.test", AccountTestHost.Password }) Assert.DoesNotContain(secret, encoded);
        Assert.Contains(traceId, encoded); Assert.Contains("maintenance.submit", encoded); Assert.Contains("office.operation.count", encoded);
        Assert.DoesNotContain("Microsoft.EntityFrameworkCore", encoded); Assert.DoesNotContain("Microsoft.AspNetCore", encoded);
    }

    [Fact]
    public async Task Native_http_client_spans_and_exported_projections_exclude_urls_events_baggage_and_error_text()
    {
        var logs = new MemoryLogs(); var identity = new TelemetryIdentity(new TestEnvironment());
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        using var sdk = Sdk.CreateTracerProviderBuilder().AddSource(OfficeTelemetry.Name).AddHttpClientInstrumentation().SetSampler(new AlwaysOnSampler())
            .AddProcessor(new SimpleActivityExportProcessor(new SafeTraceExporter(factory.CreateLogger<SafeTraceExporter>(), identity))).Build();
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start(); var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var socket = await server.AcceptTcpClientAsync(); await using var stream = socket.GetStream();
            var bytes = new byte[8192]; var length = await stream.ReadAsync(bytes);
            var headers = Encoding.ASCII.GetString(bytes, 0, length); Assert.DoesNotContain("private-baggage-canary", headers);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
        });
        using (var untrusted = new Activity("private-untrusted-name").AddBaggage("private", "private-baggage-canary").Start())
        using (var request = OfficeTelemetry.Begin(OfficeOperationKind.Http))
        {
            Activity.Current!.SetTag("url.full", "private-url-canary"); Activity.Current.SetTag("db.statement", "private-sql-canary");
            Activity.Current.AddEvent(new ActivityEvent("private-event-canary")); Activity.Current.SetStatus(ActivityStatusCode.Error, "private-error-canary");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            Assert.Equal("ok", await client.GetStringAsync($"http://127.0.0.1:{port}/private-path-canary?token=private-query-canary"));
            await serve; request.Result = OfficeOutcome.Success;
        }
        var encoded = string.Join("\n", logs.Items);
        foreach (var secret in new[] { "private-untrusted-name", "private-baggage-canary", "private-url-canary", "private-sql-canary", "private-event-canary", "private-error-canary", "private-path-canary", "private-query-canary" }) Assert.DoesNotContain(secret, encoded);
        Assert.Contains("http.client", encoded);
        Assert.Equal(default, OfficeTelemetry.Origin("invalid", "invalid")); Assert.Equal(default, OfficeTelemetry.Origin(new string('0', 32), new string('0', 16)));
    }

    [Fact]
    public async Task Full_or_failed_telemetry_export_does_not_block_a_business_commit()
    {
        using var blocked = new BlockingExporter(); using var notices = new TelemetryDropListener(NullLogger<TelemetryDropListener>.Instance);
        using var processor = new BatchActivityExportProcessor(blocked, maxQueueSize: 4, scheduledDelayMilliseconds: 1, exporterTimeoutMilliseconds: 50, maxExportBatchSize: 1);
        using var sdk = Sdk.CreateTracerProviderBuilder().AddSource(OfficeTelemetry.Name).SetSampler(new AlwaysOnSampler()).AddProcessor(processor).Build();
        try
        {
            using (var first = OfficeTelemetry.Begin(OfficeOperationKind.MaintenanceSubmit)) first.Result = OfficeOutcome.Success;
            Assert.True(blocked.Started.Wait(TimeSpan.FromSeconds(3)));
            for (var i = 0; i < 64; i++) { using var probe = OfficeTelemetry.Begin(OfficeOperationKind.MaintenanceSubmit); probe.Result = OfficeOutcome.Success; }
            await using var host = new AccountTestHost(); await host.InitializeAsync(); var (managerId, building) = await host.SeedManagerAsync();
            using var browser = host.Browser(); await host.LoginManagerAsync(browser, managerId);
            var page = await browser.GetAsync("/Portal"); Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            // A real authenticated transaction still commits while the exporter thread is blocked and its queue is full.
            using var created = await AccountTestHost.PostAsync(browser, "/Management", "/Management/AddSuite", ("BuildingId", building.ToString()), ("Name", "Committed during export outage"));
            Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
            using var scope = host.Services.CreateScope(); Assert.True(await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().Suites.AnyAsync(x => x.Name == "Committed during export outage"));
        }
        finally { blocked.Release.Set(); sdk.Shutdown(3000); }
        Assert.True(notices.Dropped > 0);
        using var synthetic = new Activity("synthetic").Start();
        var result = new SafeTraceExporter(new ThrowingLogger<SafeTraceExporter>(), new TelemetryIdentity(new TestEnvironment())).Export(new Batch<Activity>(synthetic));
        Assert.Equal(ExportResult.Failure, result);
    }

    private sealed class BlockingExporter : BaseExporter<Activity>
    {
        public ManualResetEventSlim Started { get; } = new(); public ManualResetEventSlim Release { get; } = new();
        public override ExportResult Export(in Batch<Activity> batch) { Started.Set(); Release.Wait(TimeSpan.FromSeconds(20)); return ExportResult.Failure; }
        protected override void Dispose(bool disposing) { Release.Set(); base.Dispose(disposing); }
    }
    private sealed class ThrowingLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => throw new IOException("private-export-error-canary");
    }
    public sealed class MemoryLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Items { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Capture(categoryName, Items);
        public void Dispose() { }
        private sealed class Capture(string category, ConcurrentQueue<string> items) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => items.Enqueue(category + " " + formatter(state, exception));
        }
    }
    private sealed class TestEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing"; public string ApplicationName { get; set; } = "Synthetic"; public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
