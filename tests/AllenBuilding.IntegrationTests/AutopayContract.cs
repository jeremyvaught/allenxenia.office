using AllenBuilding.Application.Access;
using AllenBuilding.Application.Billing;
using AllenBuilding.Application.Reporting;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AllenBuilding.IntegrationTests;

public static class AutopayContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        var now = new DateTimeOffset(2026, 10, 13, 7, 0, 0, TimeSpan.Zero); var clock = new Clock(now);
        var policy = new AutopayPolicy(true, "acct_autopay_contract", false);
        var payer = new OfficeUser { UserName = "autopay-" + Guid.NewGuid().ToString("N"), EmailConfirmed = true };
        var stranger = new OfficeUser { UserName = "autopay-other-" + Guid.NewGuid().ToString("N"), EmailConfirmed = true };
        var building = new Building { Name = "Autopay isolation" }; var suite = new Suite { BuildingId = building.Id, Name = "Autopay own suite" };
        var otherSuite = new Suite { BuildingId = building.Id, Name = "Autopay other suite" };
        var member = Membership.Grant(suite.Id, payer.Id, now.AddDays(-1)); var otherMember = Membership.Grant(otherSuite.Id, stranger.Id, now.AddDays(-1));
        var account = RentAccount.Open(Guid.NewGuid(), member.Id); var other = RentAccount.Open(Guid.NewGuid(), otherMember.Id);
        var terms = RentAgreement.Create(account.Id, new Money(100000), 31, new DateOnly(2026, 1, 1), null, payer.Id, now);
        var otherTerms = RentAgreement.Create(other.Id, new Money(100000), 1, new DateOnly(2026, 1, 1), null, stranger.Id, now);
        var binding = BillingCustomerBinding.Create(account.Id, policy.ProviderAccount, false, "cus_autopay_" + account.Id.ToString("N"), now);
        var otherBinding = BillingCustomerBinding.Create(other.Id, policy.ProviderAccount, false, "cus_autopay_" + other.Id.ToString("N"), now);
        var calendar = RentCalendar.ForMonth(account.Id, 2026, 11, 31); var period = BillingPeriod.Create(calendar); var debt = Obligation.Rent(period, terms, calendar, payer.Id, now);
        var older = Obligation.Issue(account.Id, new Money(1000), "Older manual debt", new DateOnly(2026, 9, 1), payer.Id, now);
        var extra = Obligation.Issue(account.Id, new Money(1000), "One-time remains manual", new DateOnly(2026, 11, 30), payer.Id, now);
        await using (var db = create())
        {
            db.AddRange(payer, stranger, building, suite, otherSuite, member, otherMember, account, other, terms, otherTerms, binding, otherBinding, period, debt, older, extra,
                new BuildingManager { BuildingId = building.Id, UserId = payer.Id }, new ActiveGrant { MembershipId = member.Id, SuiteId = suite.Id, UserId = payer.Id }, new ActiveGrant { MembershipId = otherMember.Id, SuiteId = otherSuite.Id, UserId = stranger.Id }); await db.SaveChangesAsync();
        }
        var actor = new Actor(payer.Id, new HashSet<Guid> { building.Id }, true, true);
        async Task<AutopayConsentReview?> Review(Guid id, Guid agreement, Actor? selected = null)
        { await using var db = create(); var current = new Fixed(selected ?? actor); return await Planning(db, current).ReviewAsync(id, agreement); }
        async Task<AutopayResult> Confirm(ConfirmAutopay command, Actor? selected = null)
        { await using var db = create(); var current = new Fixed(selected ?? actor); return await Planning(db, current).ConfirmAsync(command); }
        async Task<AutopayResult> Stop(StopAutopay command)
        { await using var db = create(); return await Planning(db, new Fixed(actor)).StopAsync(command); }
        AutopayPlanning Planning(OfficeDbContext db, Fixed current) => new(current, new AutopayStore(db, current, policy, Audit(db, current), clock), policy);
        AccountInvitations Audit(OfficeDbContext db, ICurrentActor current) => new(db, null!, current, new EphemeralDataProtectionProvider(), clock);
        Assert.Null(await Review(other.Id, otherTerms.Id)); // A manager+tenant never uses managed-building scope for consent.
        Assert.Null(await Review(account.Id, terms.Id, actor with { ChallengedMfa = false }));
        var reviewed = (await Review(account.Id, terms.Id))!; Assert.NotNull(reviewed); Assert.Equal(new DateOnly(2026, 11, 1), reviewed.Schedule.FirstPeriodStartsOn);
        var command = new ConfirmAutopay(reviewed.OperationId, account.Id, reviewed.AccountVersion, reviewed.Schedule, true);
        Assert.Equal(BillingCode.ValidationFailed, (await Confirm(command with { ExplicitlyAccepted = false })).Code);
        Assert.Equal(BillingCode.Conflict, (await Confirm(command with { AccountVersion = Guid.NewGuid() })).Code);
        Assert.Equal(BillingCode.Conflict, (await Confirm(command with { Schedule = reviewed.Schedule with { DueDay = 1 } })).Code);
        Assert.Equal(BillingCode.Unavailable, (await Confirm(command with { AccountId = other.Id })).Code);
        string connection; bool mysql; await using (var db = create()) { connection = db.Database.GetConnectionString()!; mysql = db is MySqlOfficeDbContext; }
        await using (var db = mysql ? (OfficeDbContext)new MySqlOfficeDbContext(new DbContextOptionsBuilder<MySqlOfficeDbContext>().UseMySQL(connection).AddInterceptors(new FailConsent()).Options)
            : new SqliteOfficeDbContext(new DbContextOptionsBuilder<SqliteOfficeDbContext>().UseSqlite(connection).AddInterceptors(new FailConsent()).Options))
            await Assert.ThrowsAsync<InvalidOperationException>(() => Planning(db, new Fixed(actor)).ConfirmAsync(command));
        await using (var db = create()) { Assert.False(await db.AutopayAuthorizations.AnyAsync(x => x.AccountId == account.Id)); Assert.False(await db.AutopaySlots.AnyAsync(x => x.AccountId == account.Id)); Assert.Equal(account.Version, (await db.RentAccounts.SingleAsync(x => x.Id == account.Id)).Version); }
        var outcomes = await Task.WhenAll(Confirm(command), Confirm(command with { OperationId = Guid.NewGuid() }));
        Assert.Single(outcomes, x => x.Code == BillingCode.Success); Assert.Single(outcomes, x => x.Code == BillingCode.Conflict);
        var result = outcomes.Single(x => x.Code == BillingCode.Success);
        ConfirmAutopay accepted;
        Guid authVersion;
        await using (var db = create())
        {
            var auth = await db.AutopayAuthorizations.SingleAsync(x => x.AccountId == account.Id); accepted = command with { OperationId = auth.OperationId }; authVersion = auth.Version;
            Assert.Equal(binding.Id, auth.CustomerBindingId); Assert.Equal(binding.CustomerId, auth.CustomerId); Assert.Equal(AutopayState.AwaitingMethod, auth.State); Assert.Null(auth.SubscriptionId); Assert.Null(auth.PaymentMethodId);
            Assert.Equal(auth.Id, (await db.AutopaySlots.SingleAsync(x => x.AccountId == account.Id)).AuthorizationId);
            var work = await db.AutopayWork.SingleAsync(x => x.AuthorizationId == auth.Id); Assert.Equal(AutopayWorkStage.Setup, work.Stage); Assert.Equal(auth.SetupOperationKey, work.ProviderOperationKey); Assert.Equal(auth.OperationId, work.OriginalOperationId);
            Assert.Equal(CollectionWorkState.Queued, work.State); Assert.Equal(auth.Version, work.ReviewedAuthorizationVersion);
            Assert.Equal(3, await db.Obligations.CountAsync(x => x.AccountId == account.Id)); Assert.False(await db.PaymentAttempts.AnyAsync(x => x.AccountId == account.Id));
            Assert.Equal(1, await db.AccountAudits.CountAsync(x => x.Action == "AutopayConsentRecorded" && x.SubjectId == auth.Id.ToString("D")));
            var current = new Fixed(actor); var manual = new CollectionStore(db, current, new(true, policy.ProviderAccount, false, new("https://autopay.example.test", "pmc_autopay")), Audit(db, current), clock);
            var balances = await manual.ReviewAsync(actor, account.Id, [debt.Id, older.Id, extra.Id], default);
            Assert.True(balances.Single(x => x.ObligationId == debt.Id).Held); Assert.False(balances.Single(x => x.ObligationId == older.Id).Held); Assert.False(balances.Single(x => x.ObligationId == extra.Id).Held);
        }
        Assert.Equal(result, await Confirm(accepted)); Assert.Equal(BillingCode.Conflict, (await Confirm(accepted with { Schedule = accepted.Schedule with { FirstDueOn = new DateOnly(2026, 12, 1) } })).Code);
        Assert.Null(await Review(account.Id, terms.Id));
        var stop = new StopAutopay(Guid.NewGuid(), result.AuthorizationId!.Value, authVersion, AutopayStopTiming.EndOfCurrentRentMonth);
        Assert.Equal(BillingCode.Conflict, (await Stop(stop with { Version = Guid.NewGuid() })).Code);
        var stopped = await Stop(stop); Assert.Equal(BillingCode.Success, stopped.Code); Assert.Equal(stopped, await Stop(stop)); Assert.Equal(BillingCode.Conflict, (await Stop(stop with { Timing = AutopayStopTiming.Immediately })).Code);
        await using (var db = create())
        {
            var auth = await db.AutopayAuthorizations.SingleAsync(x => x.Id == stop.AuthorizationId); Assert.Equal(AutopayState.StopPending, auth.State); Assert.Equal(new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.Zero), auth.StopEffectiveAt);
            Assert.Equal(2, await db.AutopayWork.CountAsync(x => x.AuthorizationId == auth.Id)); var work = await db.AutopayWork.SingleAsync(x => x.AuthorizationId == auth.Id && x.Stage == AutopayWorkStage.Stop); Assert.Equal(stop.OperationId, work.OriginalOperationId); Assert.Equal(auth.StopOperationKey, work.ProviderOperationKey);
            var current = new Fixed(actor); Assert.Single(await Planning(db, current).ReadAsync()); Assert.Empty(await Planning(db, current).ReadAsync(other.Id));
            // Replacing rent terms preserves the existing pending stop intent.
            var changedAccount = await db.RentAccounts.SingleAsync(x => x.Id == account.Id); var originalStopId = auth.StopOperationId;
            var billing = new BillingActions(current, new BillingStore(db, current, Audit(db, current), clock), clock);
            Assert.Equal(BillingCode.Success, (await billing.TermsAsync(new(Guid.NewGuid(), account.Id, changedAccount.Version, 110000, 31, new DateOnly(2026, 12, 1), ReplacesId: terms.Id))).Code);
            Assert.Equal(originalStopId, auth.StopOperationId); Assert.Equal(2, await db.AutopayWork.CountAsync(x => x.AuthorizationId == auth.Id));
            // Wrong-account reservation is rejected by the composite relation, even if bypassing the coordinator.
            db.AutopaySlots.Add(new() { AccountId = other.Id, AuthorizationId = auth.Id }); await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        // A canonical no-subscription stop permits a new consent; old work/history and keys remain distinct.
        Guid currentTermsId;
        await using (var db = create())
        {
            var original = await db.AutopayAuthorizations.SingleAsync(x => x.Id == stop.AuthorizationId);
            original.VerifyStop(new(policy.ProviderAccount, false, account.Id, binding.CustomerId, null, true, true, now));
            await db.SaveChangesAsync(); currentTermsId = await db.RentAgreements.Where(x => x.AccountId == account.Id && x.SupersededBy == null).Select(x => x.Id).SingleAsync();
        }
        var reenrollment = (await Review(account.Id, currentTermsId))!; Assert.NotNull(reenrollment); Assert.Equal(new DateOnly(2026, 12, 1), reenrollment.Schedule.FirstPeriodStartsOn);
        var nextConsent = await Confirm(new(reenrollment.OperationId, account.Id, reenrollment.AccountVersion, reenrollment.Schedule, true)); Assert.Equal(BillingCode.Success, nextConsent.Code); Assert.NotEqual(result.AuthorizationId, nextConsent.AuthorizationId);
        await using (var db = create())
        {
            var current = new Fixed(actor); var billing = new BillingActions(current, new BillingStore(db, current, Audit(db, current), clock), clock);
            var loaded = await db.RentAccounts.SingleAsync(x => x.Id == account.Id);
            Assert.Equal(BillingCode.Success, (await billing.TermsAsync(new(Guid.NewGuid(), account.Id, loaded.Version, 120000, 1, new DateOnly(2027, 1, 1), ReplacesId: currentTermsId))).Code);
            var auth = await db.AutopayAuthorizations.SingleAsync(x => x.Id == nextConsent.AuthorizationId); Assert.Equal(AutopayState.StopPending, auth.State); Assert.Equal(AutopayStopTiming.Immediately, auth.StopTiming);
            Assert.Equal(2, await db.AutopayWork.CountAsync(x => x.AuthorizationId == auth.Id)); var stopId = auth.StopOperationId;
            Assert.Equal(1, await db.AccountAudits.CountAsync(x => x.Action == "AutopayStopForChangedTerms" && x.SubjectId == auth.Id.ToString("D")));
            var membership = await db.Memberships.SingleAsync(x => x.Id == member.Id);
            Assert.Equal(BillingCode.Success, (await billing.EndAsync(new(Guid.NewGuid(), account.Id, loaded.Version, membership.Version, "Access ends independently of pending autopay cleanup"))).Code);
            Assert.NotNull((await db.Memberships.SingleAsync(x => x.Id == member.Id)).RevokedAt); Assert.False(await db.ActiveGrants.AnyAsync(x => x.MembershipId == member.Id));
            Assert.Equal(stopId, auth.StopOperationId); Assert.Equal(AutopayState.StopPending, auth.State); Assert.Equal(2, await db.AutopayWork.CountAsync(x => x.AuthorizationId == auth.Id));
            Assert.Equal(BillingClosureState.Pending, (await db.TenancyClosures.SingleAsync(x => x.AccountId == account.Id)).State);
            Assert.Equal(3, await db.Obligations.CountAsync(x => x.AccountId == account.Id));
        }
        Assert.Equal(BillingCode.Unavailable, (await Confirm(accepted)).Code); Assert.Equal(BillingCode.Unavailable, (await Stop(stop)).Code);
        await using (var db = create()) Assert.Empty(await Planning(db, new Fixed(actor)).ReadAsync());

        // A paid/adjusted first future month may be skipped, but an arbitrary later month is not silently crossed.
        var separate = await Review(other.Id, otherTerms.Id, new Actor(stranger.Id, new HashSet<Guid>(), false, false)); Assert.NotNull(separate);
        await using (var db = create())
        {
            var month = RentCalendar.ForMonth(other.Id, 2026, 11, 1); var p = BillingPeriod.Create(month); var o = Obligation.Rent(p, otherTerms, month, stranger.Id, now);
            var credit = ObligationCredit.RecordLocal(Guid.NewGuid(), stranger.Id, new(o.Id, other.Id, o.Version, 100000), 100000, ObligationCreditKind.Credit, "Paid/credited future month", now); o.FinancialStateChanged(); db.AddRange(p, o, credit); await db.SaveChangesAsync();
        }
        Assert.Equal(BillingCode.Conflict, (await Confirm(new(separate!.OperationId, other.Id, separate.AccountVersion, separate.Schedule, true), new Actor(stranger.Id, new HashSet<Guid>(), false, false))).Code);
        var skipped = await Review(other.Id, otherTerms.Id, new Actor(stranger.Id, new HashSet<Guid>(), false, false)); Assert.Equal(new DateOnly(2026, 12, 1), skipped!.Schedule.FirstPeriodStartsOn);
        await using (var db = create())
        {
            var month = RentCalendar.ForMonth(other.Id, 2027, 1, 1); var p = BillingPeriod.Create(month); var o = Obligation.Rent(p, otherTerms, month, stranger.Id, now);
            var credit = ObligationCredit.RecordLocal(Guid.NewGuid(), stranger.Id, new(o.Id, other.Id, o.Version, 100000), 1, ObligationCreditKind.Credit, "Later adjusted period", now); db.AddRange(p, o, credit); await db.SaveChangesAsync();
        }
        Assert.Null(await Review(other.Id, otherTerms.Id, new Actor(stranger.Id, new HashSet<Guid>(), false, false)));
    }
    private sealed class Fixed(Actor actor) : ICurrentActor { public Task<Actor?> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult<Actor?>(actor); }
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class FailConsent : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { if (eventData.Context!.ChangeTracker.Entries<AutopayAuthorization>().Any(x => x.State == EntityState.Added)) throw new InvalidOperationException("Synthetic consent audit failure"); return ValueTask.FromResult(result); }
    }
}
