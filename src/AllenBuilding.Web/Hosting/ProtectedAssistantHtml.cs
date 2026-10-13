using System.Text;
using AllenBuilding.Application.Assistant;
using Microsoft.AspNetCore.Mvc;

namespace AllenBuilding.Web.Hosting;

// A fully buffered, non-streaming response has one protected admission event. No replay is exposed.
public sealed class ProtectedAssistantHtml(string html, AssistantTenantScope scope, IReadOnlyList<AssistantSource> sources,
    IAssistantTenantAccess access) : IActionResult
{
    public async Task ExecuteResultAsync(ActionContext context)
    {
        var response = context.HttpContext.Response;
        var bytes = Encoding.UTF8.GetBytes(html);
        if (bytes.Length > 131072) { await Denied(response, 503, context.HttpContext.RequestAborted); return; }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.HttpContext.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var admitted = await access.ReleaseAsync(scope, sources, async token =>
            {
                response.ContentType = "text/html; charset=utf-8";
                response.Headers.CacheControl = "no-store";
                await response.Body.WriteAsync(bytes, token);
                await response.Body.FlushAsync(token);
            }, deadline.Token);
            if (!admitted) await Denied(response, 404, context.HttpContext.RequestAborted);
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or IOException or OperationCanceledException)
        {
            if (context.HttpContext.RequestAborted.IsCancellationRequested) throw;
            if (!response.HasStarted) await Denied(response, 503, context.HttpContext.RequestAborted);
        }
    }
    private static Task Denied(HttpResponse response, int status, CancellationToken cancellationToken)
    {
        response.StatusCode = status; response.ContentType = "text/html; charset=utf-8"; response.Headers.CacheControl = "no-store";
        return response.WriteAsync("<!doctype html><html lang=\"en\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Access changed</title><link rel=\"stylesheet\" href=\"/css/app.css\"><main class=\"page\"><div data-assistant-result><h1>Access changed</h1><p>This answer is no longer available. Start a new question using your current access.</p><a class=\"quiet-button\" href=\"/Assistant\">Start a new question</a></div></main></html>", cancellationToken);
    }
}
