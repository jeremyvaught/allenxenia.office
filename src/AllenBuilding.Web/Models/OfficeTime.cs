using System.Globalization;

namespace AllenBuilding.Web.Models;

// Timestamps remain UTC in storage; tenant-facing times use the building's Eastern zone.
// Calendar due/review dates are DateOnly values and must never be shifted as instants.
public static class OfficeTime
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    public static string Timestamp(DateTimeOffset? instant, bool seconds = false)
    {
        if (instant is null) return "";
        var local = TimeZoneInfo.ConvertTime(instant.Value, Zone);
        return local.ToString(seconds ? "MMM d, yyyy h:mm:ss tt" : "MMM d, yyyy h:mm tt", CultureInfo.GetCultureInfo("en-US")) +
            (Zone.IsDaylightSavingTime(local) ? " EDT" : " EST");
    }
    public static string Date(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, Zone).ToString("MMM d, yyyy", CultureInfo.GetCultureInfo("en-US"));
}
