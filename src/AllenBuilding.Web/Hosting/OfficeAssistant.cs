using AllenBuilding.Application.Assistant;
using AllenBuilding.Infrastructure.Assistant;

namespace AllenBuilding.Web.Hosting;

public static class OfficeAssistant
{
    public static void AddOfficeAssistant(this WebApplicationBuilder builder)
    {
        builder.AddLocalOpenAiConfiguration();
        var key = builder.Configuration["OPENAI_API_KEY"];
        var enabled = builder.Configuration.GetValue<bool?>("Assistant:Enabled") ?? (builder.Environment.IsDevelopment() && !string.IsNullOrWhiteSpace(key));
        if (enabled && (string.IsNullOrWhiteSpace(key) || key.Length is < 16 or > 1024 || key.Any(char.IsWhiteSpace)))
            throw new InvalidOperationException("An enabled assistant needs a server-side OpenAI credential.");
        builder.Services.AddSingleton(new AssistantPolicy(enabled));
        if (enabled)
        {
            builder.Services.AddHttpClient("OfficeAssistant", client => client.Timeout = TimeSpan.FromSeconds(55))
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
            builder.Services.AddScoped<IAssistantModel>(services => new OpenAiAssistantModel(
                services.GetRequiredService<IHttpClientFactory>().CreateClient("OfficeAssistant"), key!));
        }
        else builder.Services.AddScoped<IAssistantModel, DisabledAssistantModel>();
        builder.Services.AddScoped<AssistantHelp>();
        builder.Services.AddSingleton<AssistantBudget>();
    }
}

// Conservative initial single-process budget; production remains disabled.
// Count admission (including failures), never retry automatically, and bound partition memory.
public sealed class AssistantBudget(TimeProvider time)
{
    private readonly object _sync = new();
    private DateOnly _day;
    private int _total;
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> _hours = [];
    public bool TryAdmit(string partition)
    {
        lock (_sync)
        {
            var now = time.GetUtcNow(); var day = DateOnly.FromDateTime(now.UtcDateTime);
            if (_day != day) { _day = day; _total = 0; _hours.Clear(); }
            foreach (var expired in _hours.Where(x => now - x.Value.Start >= TimeSpan.FromHours(1)).Select(x => x.Key).ToArray()) _hours.Remove(expired);
            if (_total >= 100 || (_hours.Count >= 256 && !_hours.ContainsKey(partition))) return false;
            var counter = _hours.GetValueOrDefault(partition, (Start: now, Count: 0));
            if (counter.Count >= 20) return false;
            _hours[partition] = (counter.Start, counter.Count + 1); _total++; return true;
        }
    }
}
