using static AllenBuilding.DemoSeed.DemoContent;

namespace AllenBuilding.DemoSeed;

internal enum BillingStep { Open, Terms, Rent, Charge, Credit }

// One billing action by a manager. Month is the rent month (or the month terms start); a credit names
// its charge by rent month or by description.
internal sealed record BillingEvent(DateTimeOffset At, Person Tenant, Person By, BillingStep Step, long AmountMinorUnits = 0,
    DateOnly? Month = null, string? Description = null, DateOnly? DueOn = null, string? Reason = null);

// Rent planning for the demo: an account and terms for each tenant, rent for last month and this month,
// three one-time charges, and credits recorded by a manager for rent paid by check. The portal takes
// no payments, so nothing here touches a payment provider. Five tenants are paid up; two are behind.
internal static class DemoBilling
{
    // PaysDaysEarly: how many days before the due date this tenant's check arrives; null for the two who are behind.
    private sealed record Plan(Person Tenant, long MonthlyMinorUnits, int? PaysDaysEarly, int FirstCheck);
    private static readonly Plan[] Plans =
    [
        new(Dana, 145000, 3, 2231), new(Marcus, 160000, 2, 5107), new(Priya, 210000, 4, 1188), new(Elena, 235000, null, 4410),
        new(Thomas, 190000, 2, 3072), new(Renee, 175000, 1, 862), new(Sam, 130000, null, 0),
    ];

    public static IReadOnlyList<BillingEvent> Events(DateTimeOffset now, TimeZoneInfo zone)
    {
        var us = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var lastMonth = thisMonth.AddMonths(-1);
        DateTimeOffset Local(DateOnly date, int hour, int minute)
        {
            var local = date.ToDateTime(new TimeOnly(hour, minute));
            return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
        }
        static DateOnly Weekday(DateOnly date, int step)
        {
            while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                date = date.AddDays(step);
            return date;
        }
        static DateTimeOffset Later(DateTimeOffset first, DateTimeOffset second) => first > second ? first : second;

        var events = new List<BillingEvent>();
        for (var index = 0; index < Plans.Length; index++)
        {
            var plan = Plans[index];
            var accepted = Tenants.Single(x => x.Contact == plan.Tenant).Accepted.Resolve(now, zone);
            var joined = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(accepted, zone).DateTime);
            var opened = accepted.AddMinutes(45);
            // The six original tenants were already renting when the portal opened, so their terms cover last month.
            // Kestrel Law Office moved in partway through a month: a prorated charge first, then whole months.
            var arrived = plan.Tenant == Thomas;
            var starts = !arrived ? lastMonth : joined.Day == 1 ? joined : new DateOnly(joined.Year, joined.Month, 1).AddMonths(1);
            events.Add(new(opened, plan.Tenant, Jordan, BillingStep.Open));
            events.Add(new(opened.AddMinutes(5), plan.Tenant, Jordan, BillingStep.Terms, plan.MonthlyMinorUnits, starts));
            if (arrived)
            {
                const string prorated = "Prorated rent for the first partial month";
                events.Add(new(opened.AddMinutes(15), plan.Tenant, Jordan, BillingStep.Charge, 95000, Description: prorated, DueOn: joined.AddDays(7)));
                events.Add(new(When.Day(15, 10, 5).Resolve(now, zone), plan.Tenant, Casey, BillingStep.Credit, 95000, Description: prorated,
                    Reason: $"Prorated rent received by check {plan.FirstCheck - 1}"));
            }
            var months = new[] { lastMonth, thisMonth }.Where(x => x >= starts).ToArray();
            for (var number = 0; number < months.Length; number++)
            {
                var month = months[number];
                // Rent goes on the account five days before the month starts, or as soon as the account exists.
                var issued = Later(Local(Weekday(month.AddDays(-5), -1), 9, 30 + index), opened.AddMinutes(20 + 5 * number));
                events.Add(new(issued, plan.Tenant, Jordan, BillingStep.Rent, Month: month));
                if (plan.PaysDaysEarly is { } early)
                    events.Add(new(Later(Local(Weekday(month.AddDays(-early), -1), 10, 10 + index), issued.AddHours(1)), plan.Tenant, Casey,
                        BillingStep.Credit, plan.MonthlyMinorUnits, month, Reason: $"Rent received by check {plan.FirstCheck + number}"));
            }
        }

        // Meridian Physical Therapy paid last month six days late and has sent part of this month's rent.
        var elena = Plans.Single(x => x.Tenant == Elena);
        events.Add(new(Local(Weekday(lastMonth.AddDays(6), 1), 11, 0), Elena, Casey, BillingStep.Credit, elena.MonthlyMinorUnits, lastMonth,
            Reason: $"Rent received by check {elena.FirstCheck}, six days late"));
        var partial = Local(Weekday(thisMonth.AddDays(2), 1), 14, 20);
        if (partial < now.AddHours(-1))
            events.Add(new(partial, Elena, Casey, BillingStep.Credit, 100000, thisMonth, Reason: $"Partial payment received by check {elena.FirstCheck + 8}"));
        // Northgate Title has paid nothing for either month and has a late fee.
        events.Add(new(Local(Weekday(lastMonth.AddDays(10), 1), 9, 15), Sam, Jordan, BillingStep.Charge, 7500,
            Description: $"Late fee for {lastMonth.ToString("MMMM", us)} rent", DueOn: lastMonth.AddDays(24)));
        // A charge that is owed but not yet due.
        events.Add(new(When.Day(6, 11, 0).Resolve(now, zone), Priya, Jordan, BillingStep.Charge, 5000,
            Description: "Replacement key fobs (2)", DueOn: today.AddDays(10)));
        return events;
    }
}
