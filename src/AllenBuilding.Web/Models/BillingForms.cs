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
public sealed record BillingManagement(IReadOnlyList<BillingAccount> Accounts, IReadOnlyList<RentCandidate> Candidates);
public static class BillingDisplay
{
    public static string Usd(long minorUnits) => (minorUnits / 100m).ToString("C2", CultureInfo.GetCultureInfo("en-US"));
    public static string Label(BillingAccount account) => $"{account.Suite} · {account.PayerEmail} · membership {account.MembershipId:D}";
    public static long MinorUnits(decimal amount)
    {
        if (amount < 0.50m || amount > 999999.99m || decimal.Truncate(amount * 100m) != amount * 100m) throw new ArgumentException("Enter a USD amount with at most two decimal places.");
        return checked((long)(amount * 100m));
    }
}
