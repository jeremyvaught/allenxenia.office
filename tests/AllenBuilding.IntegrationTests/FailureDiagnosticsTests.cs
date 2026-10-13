using AllenBuilding.Web.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AllenBuilding.IntegrationTests;

public sealed class FailureDiagnosticsTests
{
    [Fact]
    public async Task Request_failures_have_correlated_types_and_call_sites_without_exception_messages_or_data()
    {
        var logs = new TelemetryTests.MemoryLogs();
        var collection = new ServiceCollection(); collection.AddLogging(builder => builder.AddProvider(logs));
        using var services = collection.BuildServiceProvider(); var context = new DefaultHttpContext { RequestServices = services };
        var exception = await Assert.ThrowsAsync<IOException>(() => OfficeObservability.RequestAsync(context, Fail));
        var output = string.Join('\n', logs.Items);
        Assert.Contains("System.IO.IOException", output); Assert.Contains("System.InvalidOperationException", output);
        Assert.Contains(nameof(Fail), output); Assert.Contains(context.Response.Headers["X-Office-TraceId"].ToString(), output);
        Assert.DoesNotContain("private-message-canary", output); Assert.DoesNotContain("private-inner-canary", output);
        Assert.DoesNotContain("private-data-canary", output);
    }
    private static Task Fail(HttpContext context)
    {
        var error = new IOException("private-message-canary", new InvalidOperationException("private-inner-canary"));
        error.Data["private-data-canary"] = "private-data-canary";
        throw error;
    }
}
