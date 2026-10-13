using System.Security.Cryptography;
using System.Text.Json;
using AllenBuilding.Application.Access;
using AllenBuilding.Domain.Billing;

namespace AllenBuilding.Application.Billing;

public enum BillingCode { Success, ValidationFailed, Unavailable, Conflict, NoFullPeriod }
public sealed record BillingResult(BillingCode Code, Guid? AccountId = null, Guid? EntityId = null, Guid? AccountVersion = null);
public sealed record OpenRentAccount(Guid OperationId, Guid AccountId, Guid MembershipId);
public sealed record SetRentTerms(Guid OperationId, Guid AccountId, Guid Version, long AmountMinorUnits, int DueDay, DateOnly StartsOn, DateOnly? EndsOn = null, Guid? ReplacesId = null);
public sealed record IssueCharge(Guid OperationId, Guid AccountId, Guid Version, long AmountMinorUnits, string Description, DateOnly DueOn);
public sealed record IssueRent(Guid OperationId, Guid AccountId, Guid Version, int Year, int Month);
public sealed record BillingAccount(Guid Id, Guid Version, string Suite, IReadOnlyList<RentTermsView> Terms, IReadOnlyList<ObligationView> Obligations, bool CollectionAvailable = false, Guid MembershipId = default, string? PayerEmail = null);
public sealed record RentTermsView(Guid Id, Guid Version, long AmountMinorUnits, string Currency, int DueDay, DateOnly StartsOn, DateOnly? EndsOn, Guid? SupersededBy);
public sealed record ObligationView(Guid Id, Guid Version, ChargeKind Kind, string Description, long IssuedMinorUnits, string Currency, DateOnly DueOn, DateOnly? PeriodStartsOn, DateOnly? PeriodEndsOn, long CreditedMinorUnits = 0)
{ public long NetChargeMinorUnits => checked(IssuedMinorUnits - CreditedMinorUnits); }

public sealed record RentCandidate(Guid MembershipId, string Suite, string Email);
public sealed record RentPreview(Guid AccountId, Guid AccountVersion, long AmountMinorUnits, DateOnly DueOn, DateOnly StartsOn, DateOnly EndsOn, bool AlreadyIssued);

public interface IBillingStore
{
    Task<IReadOnlyList<BillingAccount>> ReadAsync(Actor actor, bool management, CancellationToken cancellationToken, Guid? accountId);
    Task<IReadOnlyList<RentCandidate>> CandidatesAsync(Actor actor, CancellationToken cancellationToken);
    Task<RentPreview?> PreviewRentAsync(Actor actor, Guid accountId, int year, int month, CancellationToken cancellationToken);
    Task<BillingResult> OpenAsync(Actor actor, OpenRentAccount command, string hash, CancellationToken cancellationToken);
    Task<BillingResult> TermsAsync(Actor actor, SetRentTerms command, string hash, CancellationToken cancellationToken);
    Task<BillingResult> ChargeAsync(Actor actor, IssueCharge command, string hash, CancellationToken cancellationToken);
    Task<BillingResult> RentAsync(Actor actor, IssueRent command, string hash, CancellationToken cancellationToken);
}

// Billing planning only: no provider handoff, settlement, adjustment or autopay is enabled here.
public sealed class BillingActions(ICurrentActor current, IBillingStore store, TimeProvider time)
{
    public async Task<IReadOnlyList<BillingAccount>> ReadAsync(bool management, CancellationToken cancellationToken = default, Guid? accountId = null)
    {
        var actor = await current.GetAsync(cancellationToken);
        if (actor is null || (management && !actor.ChallengedMfa) || (actor.ManagedBuildings.Count > 0 && !actor.ChallengedMfa)) return [];
        return await store.ReadAsync(actor, management, cancellationToken, accountId);
    }

    public async Task<IReadOnlyList<RentCandidate>> CandidatesAsync(CancellationToken cancellationToken = default)
    {
        var actor = await current.GetAsync(cancellationToken);
        return actor is { ChallengedMfa: true } && actor.ManagedBuildings.Count > 0 ? await store.CandidatesAsync(actor, cancellationToken) : [];
    }
    public async Task<RentPreview?> PreviewRentAsync(Guid accountId, int year, int month, CancellationToken cancellationToken = default)
    {
        var actor = await current.GetAsync(cancellationToken);
        if (actor is not { ChallengedMfa: true } || actor.ManagedBuildings.Count == 0) return null;
        try { RentCalendar.ForMonth(accountId, year, month, 1); } catch (ArgumentException) { return null; }
        return await store.PreviewRentAsync(actor, accountId, year, month, cancellationToken);
    }

    public Task<BillingResult> OpenAsync(OpenRentAccount command, CancellationToken cancellationToken = default)
        => Execute(command, command.OperationId, command.AccountId, () => RentAccount.Open(command.AccountId, command.MembershipId),
            (actor, hash) => store.OpenAsync(actor, command, hash, cancellationToken), cancellationToken);
    public Task<BillingResult> TermsAsync(SetRentTerms command, CancellationToken cancellationToken = default)
        => Execute(command, command.OperationId, command.AccountId, () =>
        {
            Version(command.Version);
            if (command.ReplacesId is not null && (command.ReplacesId == Guid.Empty || command.StartsOn.Day != 1)) throw new ArgumentException();
            RentAgreement.Create(command.AccountId, new Money(command.AmountMinorUnits), command.DueDay, command.StartsOn, command.EndsOn, "validation", time.GetUtcNow());
        }, (actor, hash) => store.TermsAsync(actor, command, hash, cancellationToken), cancellationToken);
    public Task<BillingResult> ChargeAsync(IssueCharge command, CancellationToken cancellationToken = default)
        => Execute(command, command.OperationId, command.AccountId, () =>
        {
            Version(command.Version); Obligation.Issue(command.AccountId, new Money(command.AmountMinorUnits), command.Description, command.DueOn, "validation", time.GetUtcNow());
        }, (actor, hash) => store.ChargeAsync(actor, command, hash, cancellationToken), cancellationToken);
    public Task<BillingResult> RentAsync(IssueRent command, CancellationToken cancellationToken = default)
        => Execute(command, command.OperationId, command.AccountId, () => { Version(command.Version); RentCalendar.ForMonth(command.AccountId, command.Year, command.Month, 1); },
            (actor, hash) => store.RentAsync(actor, command, hash, cancellationToken), cancellationToken);

    private async Task<BillingResult> Execute<T>(T command, Guid operation, Guid account, Action validate, Func<Actor, string, Task<BillingResult>> action, CancellationToken cancellationToken)
    {
        var actor = await current.GetAsync(cancellationToken);
        if (actor is not { ChallengedMfa: true, RecentMfa: true } || actor.ManagedBuildings.Count == 0) return new(BillingCode.Unavailable);
        if (operation == Guid.Empty || account == Guid.Empty) return new(BillingCode.ValidationFailed);
        try { validate(); } catch (ArgumentException) { return new(BillingCode.ValidationFailed); }
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Type = typeof(T).Name, command })));
        return await action(actor, hash);
    }
    private static void Version(Guid version) { if (version == Guid.Empty) throw new ArgumentException(); }
}
