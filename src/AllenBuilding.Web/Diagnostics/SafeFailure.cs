using System.Diagnostics;
using System.Text.Json;

namespace AllenBuilding.Web.Diagnostics;

public static class SafeFailure
{
    // Never serialize Exception itself: messages, Data, parameters, paths and SQL can
    // contain credentials or private text. These bounded call sites identify the code.
    public static string Describe(Exception exception)
    {
        var errors = new List<object>();
        for (var current = exception; current is not null && errors.Count < 4; current = current.InnerException)
        {
            var frames = new StackTrace(current).GetFrames().Take(12).Select(frame =>
            {
                var method = frame.GetMethod();
                return (method?.DeclaringType?.FullName ?? "unknown") + "." + (method?.Name ?? "unknown");
            }).ToArray();
            errors.Add(new { type = current.GetType().FullName, frames });
        }
        return JsonSerializer.Serialize(errors);
    }
}
