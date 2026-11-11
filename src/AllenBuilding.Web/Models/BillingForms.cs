using System.ComponentModel.DataAnnotations;
using System.Globalization;
using AllenBuilding.Application.Billing;

namespace AllenBuilding.Web.Models;

public sealed class OpenAccountForm
{
    public Guid OperationId { get; set; }
    public Guid AccountId { get; set; }
    public Guid MembershipId { get; set; }
}
public sealed class RentTermsForm
{
    public Guid OperationId { get; set; }
    public Guid AccountId { get; set; }
    public Guid Version { get; set; }
    [Required, Range(typeof(decimal), "0.50", "999999.99")] public decimal? Amount { get; set; }
    [Range(1, 31)] public int DueDay { get; set; }
    [Required] public DateOnly? StartsOn { get; set; }
    public DateOnly? EndsOn { get; set; }
    public Guid? ReplacesId { get; set; }
}
public sealed class ChargeForm
{
    public Guid OperationId { get; set; }
    public Guid AccountId { get; set; }
    public Guid Version { get; set; }
    [Required, Range(typeof(decimal), "0.50", "999999.99")] public decimal? Amount { get; set; }
    [Required, StringLength(120)] public string Description { get; set; } = "";
    [Required] public DateOnly? DueOn { get; set; }
}
public sealed class RentPeriodForm
{
    public Guid OperationId { get; set; }
    public Guid AccountId { get; set; }
    public Guid Version { get; set; }
    [Range(1, 9998)] public int Year { get; set; }
    [Range(1, 12)] public int Month { get; set; }
}
public sealed class CreditForm
{
    public Guid OperationId { get; set; }
    public Guid AccountId { get; set; }
    public Guid ObligationId { get; set; }
    public Guid Version { get; set; }
    [Required, Range(typeof(decimal), "0.01", "999999.99")] public decimal? Amount { get; set; }
    public AllenBuilding.Domain.Billing.ObligationCreditKind Kind { get; set; }
    [Required, StringLength(2000)] public string Reason { get; set; } = "";
}
public sealed record BillingManagement(IReadOnlyList<BillingAccount> Accounts, IReadOnlyList<RentCandidate> Candidates);
public static class BillingDisplay
{
    public static string Usd(long minorUnits) => (minorUnits / 100m).ToString("C2", CultureInfo.GetCultureInfo("en-US"));
    public static string Day(DateOnly date) => date.ToString("MMM d, yyyy", CultureInfo.GetCultureInfo("en-US"));
    public static DateOnly Today(TimeProvider time) => DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTime(time.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).DateTime);
    // Monthly rent is stored as "Rent 2026-10"; people read "Rent for October 2026".
    public static string Charge(string description) =>
        description.Length == 12 && description.StartsWith("Rent ", StringComparison.Ordinal) &&
        DateOnly.TryParseExact(description[5..] + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month)
            ? "Rent for " + month.ToString("MMMM yyyy", CultureInfo.GetCultureInfo("en-US")) : description;
    public static string Ordinal(int day) => day + (day % 100 is >= 11 and <= 13 ? "th" : (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });
    // Where a charge stands today, in the words shown beside it.
    public static string Status(ObligationView charge, DateOnly today) =>
        charge.BalanceMinorUnits <= 0 ? "Settled" : charge.DueOn < today ? "Overdue" : charge.BalanceMinorUnits < charge.IssuedMinorUnits ? "Partly settled" : "Not yet due";
    public static string Label(BillingAccount account) => $"Suite {account.Suite} · {account.PayerEmail}";
    public static long MinorUnits(decimal amount, long minimumMinorUnits = 50)
    {
        if (minimumMinorUnits is < 1 or > 50 || amount < minimumMinorUnits / 100m || amount > 999999.99m || decimal.Truncate(amount * 100m) != amount * 100m)
            throw new ArgumentException("Enter a USD amount with at most two decimal places.");
        return checked((long)(amount * 100m));
    }
}
