namespace AllenBuilding.Infrastructure.Identity;

// How a person is shown. Invited accounts start as "Tenant" until the person gives a name,
// so that placeholder and a blank both count as no name and fall back to the email address.
public static class PersonName
{
    public const int MaximumLength = 120;

    public static string? Normalize(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > MaximumLength || trimmed.Any(char.IsControl) ? null : trimmed;
    }

    public static string? Of(string? displayName) =>
        string.IsNullOrWhiteSpace(displayName) || displayName == "Tenant" ? null : displayName;

    public static string Label(string? displayName, string? email) => Of(displayName) ?? email ?? "Tenant";
}
