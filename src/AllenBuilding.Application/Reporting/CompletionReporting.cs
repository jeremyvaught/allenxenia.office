using AllenBuilding.Application.Access;

namespace AllenBuilding.Application.Reporting;

public sealed record CompletionQuery(Guid? BuildingId = null)
{
    public bool IsValid => BuildingId != Guid.Empty;
}
public sealed record DurationSummary(long Samples, double? MedianSeconds, double? P95Seconds);
public sealed record CompletionReport(CompletionQuery Query, DateTimeOffset ObservedAt, long Cycles, long Unresolved, long Cancelled,
    long AmbiguousRequests, DurationSummary Completion, DurationSummary Verification, IReadOnlyList<ReportBuilding> Buildings);
public interface ICompletionReportReader
{
    Task<CompletionReport?> ReadAsync(Actor actor, CompletionQuery query, DateTimeOffset observedAt, CancellationToken cancellationToken);
}
public sealed class CompletionReportQueries(ICurrentActor current, ICompletionReportReader reader, TimeProvider time)
{
    public async Task<CompletionReport?> ReadAsync(CompletionQuery query, CancellationToken token = default)
    {
        if (!query.IsValid) return null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            var actor = await current.GetAsync(linked.Token);
            if (actor is not { ChallengedMfa: true } || actor.ManagedBuildings.Count == 0) return null;
            var report = await reader.ReadAsync(actor, query, time.GetUtcNow(), linked.Token);
            var fresh = await current.GetAsync(linked.Token);
            return report is not null && fresh is { ChallengedMfa: true } && fresh.UserId == actor.UserId && report.Buildings.All(x => fresh.ManagedBuildings.Contains(x.Id)) ? report : null;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
        { throw new TimeoutException("The management report exceeded its deadline."); }
    }
}
