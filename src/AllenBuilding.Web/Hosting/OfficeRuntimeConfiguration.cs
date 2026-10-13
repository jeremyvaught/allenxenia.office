namespace AllenBuilding.Web.Hosting;

public static class OfficeRuntimeConfiguration
{
    public static void AddOfficeRuntimeConfiguration(this WebApplicationBuilder builder, bool migrations)
    {
        var configured = builder.Configuration["Office:RuntimeConfigFile"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                if (!Path.IsPathFullyQualified(configured)) throw new InvalidOperationException();
                var path = Path.GetFullPath(configured); var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(builder.Environment.ContentRootPath));
                if (path.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidOperationException();
                var file = new FileInfo(path); var parent = file.Directory!;
                if (!file.Exists || file.Length is < 2 or > 1048576 || file.LinkTarget is not null || parent.LinkTarget is not null) throw new InvalidOperationException();
                if (!OperatingSystem.IsWindows())
                {
                    const UnixFileMode shared = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                    if ((File.GetUnixFileMode(path) & shared) != 0 || (File.GetUnixFileMode(parent.FullName) & shared) != 0 ||
                        (File.GetUnixFileMode(path) & UnixFileMode.UserRead) == 0) throw new InvalidOperationException();
                }
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var bytes = new byte[checked((int)input.Length)]; if (bytes.Length is < 2 or > 1048576) throw new InvalidOperationException();
                input.ReadExactly(bytes); if (input.ReadByte() != -1) throw new InvalidOperationException();
                using var snapshot = new MemoryStream(bytes, writable: false);
                builder.Configuration.AddJsonStream(snapshot);
                // Explicit process environment remains the final override, including emergency feature disablement.
                builder.Configuration.AddEnvironmentVariables();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or FormatException or System.Text.Json.JsonException)
            { throw new InvalidOperationException("Runtime configuration must be a valid, bounded private file outside the release directory; Unix file/parent permissions must be owner-only (normally 0600/0700)."); }
        }
        if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing") && !migrations &&
            !string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("OfficeMigrator")))
            throw new InvalidOperationException("The long-running application must not receive the migration credential. Supply it only to the controlled migration process.");
    }
}
