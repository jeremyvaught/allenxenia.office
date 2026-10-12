namespace AllenBuilding.Domain.Billing;

public readonly record struct RentPeriod(Guid AccountId, DateOnly StartsOn, DateOnly EndsOn, DateOnly DueOn);

public static class RentCalendar
{
    public static RentPeriod ForMonth(Guid accountId, int year, int month, int dueDay)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("A rent account is required.");
        if (dueDay is < 1 or > 31) throw new ArgumentOutOfRangeException(nameof(dueDay));
        var start = new DateOnly(year, month, 1);
        var end = start.AddMonths(1);
        var due = new DateOnly(year, month, Math.Min(dueDay, DateTime.DaysInMonth(year, month)));
        return new RentPeriod(accountId, start, end, due);
    }
}
