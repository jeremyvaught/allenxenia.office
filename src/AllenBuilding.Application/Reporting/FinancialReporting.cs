using AllenBuilding.Application.Diagnostics;

namespace AllenBuilding.Application.Reporting;

public enum RentBalanceGroup { Outstanding, Overdue, Held }
public sealed record RentBalanceQuery(Guid? BuildingId = null, RentBalanceGroup Group = RentBalanceGroup.Outstanding, int Page = 1)
{
    public const int PageSize = 50;
    public bool IsValid => BuildingId != Guid.Empty && Enum.IsDefined(Group) && Page is >= 1 and <= 1000;
}
public sealed record RentBalanceTotals(long Count, long ConfirmedMinor, long OverdueMinor, long HeldMinor,
    long From1To30DaysMinor, long From31To60DaysMinor, long From61To90DaysMinor, long Over90DaysMinor,
    long OpenExceptions, long UnresolvedRefundMinor, long UnresolvedDisputeWithdrawalMinor)
{
    public long UnheldMinor => ConfirmedMinor - HeldMinor;
}
public sealed record RentBalanceItem(Guid ObligationId, Guid AccountId, string Building, string Suite, string Description,
    DateOnly DueOn, long ConfirmedMinor, bool Held);
public sealed record RentBalanceReport(RentBalanceQuery Query, DateTimeOffset ObservedAt, DateOnly LocalDate,
    RentBalanceTotals Totals, IReadOnlyList<RentBalanceItem> Items, bool HasNext, IReadOnlyList<ReportBuilding> Buildings);

public sealed partial class ManagementReportQueries
{
    public async Task<RentBalanceReport?> RentBalancesAsync(RentBalanceQuery query, CancellationToken cancellationToken = default)
    {
        using var operation = OfficeTelemetry.Begin(OfficeOperationKind.RentBalances); operation.Result = OfficeOutcome.Denied;
        if (!query.IsValid) { operation.Result = OfficeOutcome.Invalid; return null; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var actor = await current.GetAsync(linked.Token);
            if (actor is not { ChallengedMfa: true } || actor.ManagedBuildings.Count == 0) return null;
            operation.Result = OfficeOutcome.Failure;
            var now = time.GetUtcNow();
            var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).DateTime);
            var report = await reader.RentBalancesAsync(actor, query, now, localDate, linked.Token);
            var fresh = await current.GetAsync(linked.Token);
            var allowed = report is not null && fresh is { ChallengedMfa: true } && fresh.UserId == actor.UserId && report.Buildings.All(x => fresh.ManagedBuildings.Contains(x.Id));
            operation.Result = allowed ? OfficeOutcome.Success : OfficeOutcome.Denied; return allowed ? report : null;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        { operation.Result = OfficeOutcome.Failure; throw new TimeoutException("The management report exceeded its deadline."); }
        catch (OperationCanceledException) { operation.Result = OfficeOutcome.Cancelled; throw; }
    }
}
