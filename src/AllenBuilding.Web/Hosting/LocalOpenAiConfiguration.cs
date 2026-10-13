namespace AllenBuilding.Web.Hosting;

public static class LocalOpenAiConfiguration
{
    public static void AddLocalOpenAiConfiguration(this WebApplicationBuilder builder)
    {
        // Local source checkout only. Published/Testing/Production never searches developer env files.
        if (!builder.Environment.IsDevelopment()) return;
        var root = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "../.."));
        if (!File.Exists(Path.Combine(root, "AGENTS.md"))) return;
        var path = Path.Combine(root, ".env.local");
        var key = ReadKey(path);
        if (key is not null && string.IsNullOrWhiteSpace(builder.Configuration["OPENAI_API_KEY"]))
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["OPENAI_API_KEY"] = key });
    }

    public static string? ReadKey(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var file = new FileInfo(path);
            if (file.LinkTarget is not null || file.Length > 16384) throw new InvalidOperationException();
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new InvalidOperationException();
            var keys = File.ReadAllLines(path).Select(x => x.Trim()).Where(x => x.StartsWith("OPENAI_API_KEY=", StringComparison.Ordinal)).ToArray();
            if (keys.Length == 0) return null;
            if (keys.Length != 1) throw new InvalidOperationException();
            var value = keys[0]["OPENAI_API_KEY=".Length..].Trim();
            if (value.Length >= 2 && ((value[0] == '\"' && value[^1] == '\"') || (value[0] == '\'' && value[^1] == '\''))) value = value[1..^1];
            if (value.Length is < 16 or > 1024 || value.Any(char.IsWhiteSpace)) throw new InvalidOperationException();
            return value;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { throw new InvalidOperationException("Local OpenAI configuration must contain one key in an owner-only, bounded regular .env.local file."); }
    }
}
