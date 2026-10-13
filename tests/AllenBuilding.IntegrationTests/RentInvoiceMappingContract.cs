using AllenBuilding.Application.Access;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AllenBuilding.IntegrationTests;

public static class RentInvoiceMappingContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        foreach (var scenario in new[] { "new", "existing", "competing", "duplicate-period", "wrong-owner", "changed-total", "paid-status", "revoked", "stopped", "adjusted", "manual-attempt", "replacement", "save-failure" })
        {
            var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero); var clock = new Clock(now); var user = new OfficeUser { UserName = "invoice-" + Guid.NewGuid().ToString("N"), EmailConfirmed = true };
            var building = new Building { Name = "Invoice lineage" }; var suite = new Suite { BuildingId = building.Id, Name = "Original rent suite" };
            var member = Membership.Grant(suite.Id, user.Id, now.AddDays(-1)); var account = RentAccount.Open(Guid.NewGuid(), member.Id);
            var terms = RentAgreement.Create(account.Id, new Money(100000), 31, new(2026, 1, 1), null, user.Id, now);
            var binding = BillingCustomerBinding.Create(account.Id, "acct_invoice_contract", false, "cus_" + account.Id.ToString("N"), now);
            var auth = AutopayAuthorization.Consent(Guid.NewGuid(), user.Id, account, binding, AutopaySchedule.Select(terms, now, []), true, now);
            auth.ApplyVerifiedMethod(binding.ProviderAccount, false, binding.CustomerId, "pm_original", auth.ConsentHash);
            auth.ApplyVerifiedSubscription(new(binding.ProviderAccount, false, account.Id, binding.CustomerId, "sub_" + auth.Id.ToString("N"), "pm_original", auth.ConsentHash, 100000, "USD", 31, 17, auth.FirstAnchorUtc, true, now));
            var fact = new CanonicalRentInvoice(binding.ProviderAccount, false, account.Id, auth.Id, binding.CustomerId, auth.SubscriptionId!, auth.ConsentHash, "in_" + auth.Id.ToString("N"), "il_" + auth.Id.ToString("N"), new(2026, 11, 30, 17, 0, 0, TimeSpan.Zero), new(2026, 12, 31, 17, 0, 0, TimeSpan.Zero), 100000, 100000, 100000, "USD", RentInvoiceState.Open, true, now, now);
            var period = BillingPeriod.Create(RentCalendar.ForMonth(account.Id, 2026, 11, 31)); var debt = Obligation.Rent(period, terms, RentCalendar.ForMonth(account.Id, 2026, 11, 31), user.Id, now);
            var hasDebt = scenario is "existing" or "adjusted" or "manual-attempt" or "replacement";
            await using (var db = create())
            {
                db.AddRange(user, building, suite, member, account, terms, binding, auth, new AutopaySlot { AccountId = account.Id, AuthorizationId = auth.Id }, new ActiveGrant { MembershipId = member.Id, SuiteId = suite.Id, UserId = user.Id });
                if (hasDebt) db.AddRange(period, debt);
                if (scenario == "adjusted") db.Add(ObligationCredit.RecordLocal(Guid.NewGuid(), user.Id, new(debt.Id, account.Id, debt.Version, 100000), 20000, ObligationCreditKind.Credit, "Original reviewed credit", now));
                if (scenario == "manual-attempt") db.Add(PaymentAttempt.Admit(Guid.NewGuid(), user.Id, binding, [new(debt.Id, account.Id, debt.Version, 100000)], now));
                if (scenario is "revoked" or "stopped") { member.Revoke(user.Id, "Access ends independently", now); db.ActiveGrants.Remove(db.ActiveGrants.Local.Single()); }
                if (scenario == "stopped") { auth.RequestStop(Guid.NewGuid(), user.Id, AutopayStopTiming.Immediately, now); auth.VerifyStop(new(binding.ProviderAccount, false, account.Id, binding.CustomerId, auth.SubscriptionId, true, true, now)); }
                if (scenario == "replacement") { var replacement = RentAgreement.Create(account.Id, new Money(120000), 1, new(2026, 11, 1), null, user.Id, now); terms.Supersede(replacement); db.Add(replacement); }
                await db.SaveChangesAsync();
            }
            async Task<InvoiceMappingResult> Apply(CanonicalRentInvoice selected) { await using var db = create(); return await new RentInvoiceMappingStore(db, clock).ApplyAsync(selected, default); }
            if (scenario == "wrong-owner") fact = fact with { CustomerId = "cus_other" };
            if (scenario == "changed-total") fact = fact with { TotalMinorUnits = 80000 };
            if (scenario == "paid-status") fact = fact with { State = RentInvoiceState.Paid, RemainingMinorUnits = 0, AutoAdvance = false };
            if (scenario == "save-failure")
            {
                string connection; bool mysql; await using (var db = create()) { connection = db.Database.GetConnectionString()!; mysql = db is MySqlOfficeDbContext; }
                await using var failing = mysql ? (OfficeDbContext)new MySqlOfficeDbContext(new DbContextOptionsBuilder<MySqlOfficeDbContext>().UseMySQL(connection).AddInterceptors(new FailInvoice()).Options)
                    : new SqliteOfficeDbContext(new DbContextOptionsBuilder<SqliteOfficeDbContext>().UseSqlite(connection).AddInterceptors(new FailInvoice()).Options);
                await Assert.ThrowsAsync<InvalidOperationException>(() => new RentInvoiceMappingStore(failing, clock).ApplyAsync(fact, default));
            }
            else
            {
                InvoiceMappingResult result;
                if (scenario == "competing") { var results = await Task.WhenAll(Apply(fact), Apply(fact)); Assert.All(results, x => Assert.Equal(InvoiceMappingCode.Recorded, x.Code)); Assert.Equal(results[0].MappingId, results[1].MappingId); result = results[0]; }
                else result = await Apply(fact);
                var expected = scenario is "wrong-owner" or "changed-total" ? InvoiceMappingCode.Invalid : scenario is "adjusted" or "manual-attempt" or "replacement" ? InvoiceMappingCode.Conflict : InvoiceMappingCode.Recorded;
                Assert.Equal(expected, result.Code);
                if (expected == InvoiceMappingCode.Recorded)
                {
                    Assert.Equal(result, await Apply(fact));
                    if (scenario == "duplicate-period") Assert.Equal(InvoiceMappingCode.Conflict, (await Apply(fact with { InvoiceId = "in_duplicate" })).Code);
                    if (scenario == "existing") Assert.Equal(debt.Id, result.ObligationId);
                    await using var db = create(); var mapped = await db.ProviderRentInvoices.SingleAsync(x => x.Id == result.MappingId);
                    Assert.Equal(new DateOnly(2026, 11, 1), (await db.BillingPeriods.SingleAsync(x => x.Id == mapped.PeriodId)).StartsOn);
                    Assert.Equal(100000, (await db.Obligations.SingleAsync(x => x.Id == mapped.ObligationId)).AmountMinorUnits);
                    if (scenario is not ("revoked" or "stopped"))
                    {
                        // The durable instruction holds unrelated Checkout even after the authorization is stopped.
                        var stored = await db.AutopayAuthorizations.SingleAsync(x => x.Id == auth.Id); stored.RequestStop(Guid.NewGuid(), user.Id, AutopayStopTiming.Immediately, now); stored.VerifyStop(new(binding.ProviderAccount, false, account.Id, binding.CustomerId, auth.SubscriptionId, true, true, now)); await db.SaveChangesAsync();
                        var actor = new Actor(user.Id, new HashSet<Guid>(), false, false); var current = new Fixed(actor); var policy = new CollectionPolicy(true, binding.ProviderAccount, false, new CheckoutProfile("https://invoice.example.test", "pmc_invoice"));
                        var review = await new CollectionStore(db, current, policy, new AccountInvitations(db, null!, current, new EphemeralDataProtectionProvider(), clock), clock).ReviewAsync(actor, account.Id, [mapped.ObligationId], default);
                        Assert.True(Assert.Single(review).HasProviderInstruction);
                    }
                }
            }
            await using (var check = create())
            {
                var count = scenario is "wrong-owner" or "changed-total" or "adjusted" or "manual-attempt" or "replacement" or "save-failure" ? 0 : 1;
                Assert.Equal(count, await check.ProviderRentInvoices.CountAsync(x => x.AccountId == account.Id));
                Assert.Equal(hasDebt || count > 0 ? 1 : 0, await check.Obligations.CountAsync(x => x.AccountId == account.Id));
                Assert.False(await check.SettlementPayments.AnyAsync(x => x.AccountId == account.Id)); Assert.False(await check.PaymentAllocations.AnyAsync(x => x.AccountId == account.Id));
            }
        }
    }
    private sealed class Fixed(Actor actor) : ICurrentActor { public Task<Actor?> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult<Actor?>(actor); }
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class FailInvoice : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { if (eventData.Context!.ChangeTracker.Entries<ProviderRentInvoice>().Any(x => x.State == EntityState.Added)) throw new InvalidOperationException("Synthetic invoice audit failure"); return ValueTask.FromResult(result); }
    }
}
