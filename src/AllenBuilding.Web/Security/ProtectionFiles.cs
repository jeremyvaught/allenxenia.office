namespace AllenBuilding.Web.Security;

internal static class ProtectionFiles
{
    public static void ValidateDirectory(string path, string release)
    {
        try
        {
            Outside(path, release);
            var directory = new DirectoryInfo(path); var parent = directory.Parent!;
            // A deployment owns the private parent; application startup may create only its key subdirectory.
            if (!parent.Exists || parent.LinkTarget is not null || directory.LinkTarget is not null) throw new InvalidOperationException();
            Private(parent.FullName, directory: true);
            if (directory.Exists) Private(directory.FullName, directory: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { throw new InvalidOperationException("Protected keys require an absolute persistent directory outside releases with a private existing parent and owner-only permissions."); }
    }

    public static void ValidateCertificate(string path, string release)
    {
        Outside(path, release);
        var file = new FileInfo(path); var parent = file.Directory!;
        if (!file.Exists || file.Length is < 1 or > 1048576 || file.LinkTarget is not null || parent.LinkTarget is not null) throw new InvalidOperationException();
        Private(parent.FullName, directory: true); Private(file.FullName, directory: false);
    }

    private static void Outside(string path, string release)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(release)); var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (full.Equals(root, comparison) || full.StartsWith(root + Path.DirectorySeparatorChar, comparison)) throw new InvalidOperationException();
    }

    private static void Private(string path, bool directory)
    {
        if (OperatingSystem.IsWindows()) return;
        const UnixFileMode shared = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        var mode = File.GetUnixFileMode(path);
        if ((mode & shared) != 0 || (mode & UnixFileMode.UserRead) == 0 || (directory && (mode & UnixFileMode.UserExecute) == 0)) throw new InvalidOperationException();
    }
}
