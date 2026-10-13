using AllenBuilding.Web.Models;

namespace AllenBuilding.IntegrationTests;

public sealed class OfficeTimeTests
{
    [Theory]
    [InlineData("2026-01-15T15:00:00Z", "Jan 15, 2026 10:00 AM EST")]
    [InlineData("2026-07-15T15:00:00Z", "Jul 15, 2026 11:00 AM EDT")]
    [InlineData("2026-11-01T05:30:00Z", "Nov 1, 2026 1:30 AM EDT")]
    [InlineData("2026-11-01T06:30:00Z", "Nov 1, 2026 1:30 AM EST")]
    public void Building_time_uses_the_correct_offset_including_repeated_fall_hours(string utc, string displayed) =>
        Assert.Equal(displayed, OfficeTime.Timestamp(DateTimeOffset.Parse(utc)));

    [Fact]
    public void Local_date_changes_before_utc_midnight_and_missing_times_remain_empty()
    {
        Assert.Equal("Oct 13, 2026", OfficeTime.Date(DateTimeOffset.Parse("2026-10-14T01:00:00Z")));
        Assert.Equal("", OfficeTime.Timestamp(null));
    }
}
