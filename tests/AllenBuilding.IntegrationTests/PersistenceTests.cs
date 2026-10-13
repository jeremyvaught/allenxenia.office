using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Maintenance;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AllenBuilding.IntegrationTests;

public sealed class PersistenceTests
{
    [Fact]
    public async Task Sqlite_migrations_constraints_and_concurrency_preserve_private_ownership()
    {
        var path = Path.Combine(Path.GetTempPath(), $"office-check-{Guid.NewGuid():N}.db");
        OfficeDbContext Create() => new SqliteOfficeDbContext(new DbContextOptionsBuilder<SqliteOfficeDbContext>()
            .UseSqlite($"Data Source={path};Foreign Keys=True").Options);
        try { await VerifyDatabase(Create); }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    [MySqlFact]
    public async Task MySql_migrations_constraints_and_concurrency_match_local_contract()
    {
        var connection = Environment.GetEnvironmentVariable("OFFICE_TEST_MYSQL")!;
        var parsed = new MySql.Data.MySqlClient.MySqlConnectionStringBuilder(connection);
        Assert.Equal("office_check", parsed.Database);
        Assert.Contains(parsed.Server, new[] { "127.0.0.1", "localhost" });
        OfficeDbContext Create() => new MySqlOfficeDbContext(new DbContextOptionsBuilder<MySqlOfficeDbContext>()
            .UseMySQL(connection).Options);
        await VerifyDatabase(Create);
        var runtime = Environment.GetEnvironmentVariable("OFFICE_TEST_MYSQL_RUNTIME");
        if (!string.IsNullOrWhiteSpace(runtime)) await MySqlRuntimeRoleContract.VerifyAsync(runtime);
    }

    private static async Task VerifyDatabase(Func<OfficeDbContext> create)
    {
        var now = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        var user = new OfficeUser { Id = Guid.NewGuid().ToString("N"), UserName = Guid.NewGuid().ToString("N") };
        var building = new Building { Name = "Synthetic test building" };
        var suite = new Suite { BuildingId = building.Id, Name = "Test suite" };
        var membership = Membership.Grant(suite.Id, user.Id, now.AddDays(-1));
        var request = WorkOrder.Submit(suite.Id, membership.Id, user.Id, "Leaking sink", "Water under basin", "Kitchen", now);
        await using (var db = create())
        {
            // Exercise upgrades with a real pre-delivery row, not only an empty database.
            await db.GetService<IMigrator>().MigrateAsync("MaintenanceWorkflow");
            var legacyUser = new OfficeUser { UserName = $"upgrade-{Guid.NewGuid():N}@example.test" };
            db.Users.Add(legacyUser); await db.SaveChangesAsync();
            var messageId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO account_messages (id, user_id, kind, protected_payload, created_at) VALUES ({messageId.ToString("D")}, {legacyUser.Id}, {"SecurityNotice"}, {"synthetic-legacy-payload"}, {now.UtcDateTime})");
            await db.GetService<IMigrator>().MigrateAsync("PaymentEventInbox");
            var oldEventId = Guid.NewGuid(); var oldEventVersion = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO payment_events (id, provider_account, live_mode, event_id, event_type, resource_id, api_version, payload_hash, state, received_at, version) VALUES ({oldEventId.ToString("D")}, {"acct_upgrade"}, {false}, {"evt_upgrade"}, {"checkout.session.completed"}, {"cs_upgrade"}, {"2026-09-30.endive"}, {new string('A', 64)}, {"Pending"}, {now.UtcDateTime}, {oldEventVersion.ToString("D")})");
            await db.GetService<IMigrator>().MigrateAsync("DurableCollectionWork");
            var oldBuilding = new Building { Name = "Legacy dispatch upgrade" }; var oldSuite = new Suite { BuildingId = oldBuilding.Id, Name = "Legacy office" };
            var oldMembership = Membership.Grant(oldSuite.Id, legacyUser.Id, now.AddDays(-1)); var oldAccount = RentAccount.Open(Guid.NewGuid(), oldMembership.Id);
            var oldBinding = BillingCustomerBinding.Create(oldAccount.Id, "acct_upgrade", false, "cus_upgrade", now);
            var oldDebt = Obligation.Issue(oldAccount.Id, new Money(1000), "Legacy dispatch debt", new DateOnly(2026, 10, 1), legacyUser.Id, now);
            var oldAttempt = PaymentAttempt.Admit(Guid.NewGuid(), legacyUser.Id, oldBinding, [new(oldDebt.Id, oldAccount.Id, oldDebt.Version, 1000)], now);
            db.AddRange(oldBuilding, oldSuite, oldMembership); await db.SaveChangesAsync();
            // Seed the actual old schema, rather than inserting a new-model nullable column before its migration.
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO rent_accounts (id, membership_id, version) VALUES ({oldAccount.Id.ToString("D")}, {oldMembership.Id.ToString("D")}, {oldAccount.Version.ToString("D")})");
            db.AddRange(oldBinding, oldDebt, oldAttempt); await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO collection_work (attempt_id, api_version, public_origin, payment_method_configuration, state, lease_token, next_attempt_at, created_at, attempts, failures) VALUES ({oldAttempt.Id.ToString("D")}, {CheckoutProfile.ApiContract}, {"https://upgrade.example.test"}, {"pmc_upgrade"}, {"Queued"}, {Guid.Empty.ToString("D")}, {now.UtcDateTime}, {now.UtcDateTime}, {0}, {0})");
            await db.GetService<IMigrator>().MigrateAsync("CanonicalCashMovements");
            // Populated upgrade: original capture, pending returned-cash review and a pre-existing local credit survive the new composite relations.
            var cashSuite = new Suite { BuildingId = oldBuilding.Id, Name = "Cash schema upgrade" }; var cashMembership = Membership.Grant(cashSuite.Id, legacyUser.Id, now.AddDays(-1));
            var cashAccount = RentAccount.Open(Guid.NewGuid(), cashMembership.Id); var cashBinding = BillingCustomerBinding.Create(cashAccount.Id, "acct_upgrade_cash", false, "cus_upgrade_cash", now);
            var cashDebt = Obligation.Issue(cashAccount.Id, new Money(1000), "Captured upgrade charge", new DateOnly(2026, 10, 1), legacyUser.Id, now);
            var creditedDebt = Obligation.Issue(cashAccount.Id, new Money(1000), "Earlier credited charge", new DateOnly(2026, 9, 1), legacyUser.Id, now);
            var credit = ObligationCredit.RecordLocal(Guid.NewGuid(), legacyUser.Id, new(creditedDebt.Id, cashAccount.Id, creditedDebt.Version, 1000), 1, ObligationCreditKind.Credit, "Pre-existing local credit", now);
            var cashAttempt = PaymentAttempt.Admit(Guid.NewGuid(), legacyUser.Id, cashBinding, [new(cashDebt.Id, cashAccount.Id, cashDebt.Version, 1000)], now); cashAttempt.BeginDispatch(now);
            var cashCapture = SettlementPayment.Record(cashAttempt, new(cashBinding.ProviderAccount, false, cashBinding.CustomerId, "cs_upgrade_cash", "pi_upgrade_cash", 1000, "USD"), now);
            var movement = CashMovement.Record(cashCapture, cashAttempt, new(cashBinding.ProviderAccount, false, cashAccount.Id, cashAttempt.Id, cashBinding.CustomerId, cashAttempt.PaymentId!, "re_upgrade_cash", "txn_upgrade_cash", CashMovementKind.Refund, -100, "USD", now), now);
            var cashReview = CashMovementReview.Open(movement);
            db.AddRange(cashSuite, cashMembership); await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO rent_accounts (id, membership_id, version) VALUES ({cashAccount.Id.ToString("D")}, {cashMembership.Id.ToString("D")}, {cashAccount.Version.ToString("D")})");
            db.AddRange(cashBinding, cashDebt, creditedDebt, cashAttempt, cashCapture, movement); await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO cash_movement_reviews (movement_id, account_id, state, version, created_at) VALUES ({movement.Id.ToString("D")}, {cashAccount.Id.ToString("D")}, {"Pending"}, {cashReview.Version.ToString("D")}, {now.UtcDateTime})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO obligation_credits (id, account_id, obligation_id, reviewed_version, operation_id, actor_id, kind, amount_minor_units, currency, reason, recorded_at) VALUES ({credit.Id.ToString("D")}, {cashAccount.Id.ToString("D")}, {creditedDebt.Id.ToString("D")}, {credit.ReviewedVersion.ToString("D")}, {credit.OperationId.ToString("D")}, {legacyUser.Id}, {"Credit"}, {1L}, {"USD"}, {credit.Reason}, {now.UtcDateTime})");
            await db.Database.MigrateAsync();
            var restoredReview = await db.CashMovementReviews.AsNoTracking().SingleAsync(x => x.MovementId == movement.Id);
            Assert.Equal(CashMovementReviewState.Pending, restoredReview.State); Assert.Equal(cashReview.Version, restoredReview.Version); Assert.Null(restoredReview.OperationId); Assert.Null(restoredReview.Disposition); Assert.Null(restoredReview.VerifiedAt); Assert.Null(restoredReview.AgreementHash);
            var restoredCredit = await db.ObligationCredits.AsNoTracking().SingleAsync(x => x.Id == credit.Id); Assert.Equal(1, restoredCredit.AmountMinorUnits); Assert.Null(restoredCredit.SourceMovementId);
            Assert.Equal(-100, (await db.CashMovements.AsNoTracking().SingleAsync(x => x.Id == movement.Id)).SignedMinorUnits);
            Assert.Equal(1000, (await db.SettlementPayments.AsNoTracking().SingleAsync(x => x.Id == cashCapture.Id)).AmountMinorUnits);
            Assert.Equal(1000, (await db.PaymentAllocations.AsNoTracking().SingleAsync(x => x.PaymentId == cashCapture.Id)).AmountMinorUnits);
            var oldWork = await db.CollectionWork.AsNoTracking().SingleAsync(x => x.AttemptId == oldAttempt.Id);
            Assert.Equal(now, oldWork.CycleStartedAt); Assert.False(oldWork.ReadOnlyReconciliation); Assert.Equal("pmc_upgrade", oldWork.PaymentMethodConfiguration);
            var oldEvent = await db.PaymentEvents.AsNoTracking().SingleAsync(x => x.Id == oldEventId);
            Assert.Equal(now, oldEvent.NextAttemptAt); Assert.Equal(Guid.Empty, oldEvent.LeaseToken); Assert.Null(oldEvent.LeaseUntil); Assert.Equal(0, oldEvent.Failures);
            var upgraded = await db.AccountMessages.AsNoTracking().SingleAsync(x => x.Id == messageId);
            Assert.Equal(MessageState.Queued, upgraded.State); Assert.Equal(Guid.Empty, upgraded.LeaseToken);
            Assert.Equal(upgraded.CreatedAt, upgraded.NextAttemptAt);
            Assert.Null(upgraded.OriginTraceId); Assert.Null(upgraded.OriginSpanId); Assert.Null(upgraded.OriginOperationId);
            Assert.Null((await db.RentAccounts.SingleAsync(x => x.Id == oldAccount.Id)).EndedAt);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            db.AddRange(user, building, suite, membership, request,
                new ActiveGrant { UserId = user.Id, SuiteId = suite.Id, MembershipId = membership.Id });
            await db.SaveChangesAsync();
        }
        await using (var db = create())
        {
            var active = await db.Memberships.Where(x => x.UserId == user.Id && x.RevokedAt == null && x.StartsAt <= now)
                .SingleAsync();
            Assert.Equal(membership.Id, active.Id);
            Assert.Equal(now, (await db.WorkOrders.SingleAsync(x => x.Id == request.Id)).CreatedAt);
            Assert.Equal(user.Id, (await db.WorkOrders.SingleAsync(x => x.Id == request.Id)).RequesterUserId);
        }
        await using (var first = create())
        await using (var stale = create())
        {
            var a = await first.WorkOrders.SingleAsync(x => x.Id == request.Id);
            var b = await stale.WorkOrders.SingleAsync(x => x.Id == request.Id);
            a.Triage(Priority.High, Trade.Plumbing, now); b.Triage(Priority.Low, Trade.General, now);
            await first.SaveChangesAsync();
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
        }
        await using (var db = create())
        {
            var duplicate = Membership.Grant(suite.Id, user.Id, now);
            db.Memberships.Add(duplicate);
            db.ActiveGrants.Add(new ActiveGrant { UserId = user.Id, SuiteId = suite.Id, MembershipId = duplicate.Id });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using (var db = create())
        {
            Assert.Equal(1, await db.Memberships.CountAsync(x => x.UserId == user.Id));
            db.WorkOrders.Add(WorkOrder.Submit(suite.Id, Guid.NewGuid(), user.Id, "Invalid original grant", "Description", "Kitchen", now));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using (var db = create())
        {
            var wrongRequester = new OfficeUser { Id = Guid.NewGuid().ToString("N"), UserName = Guid.NewGuid().ToString("N") };
            db.Users.Add(wrongRequester);
            await db.SaveChangesAsync();
            db.WorkOrders.Add(WorkOrder.Submit(suite.Id, membership.Id, wrongRequester.Id, "Mismatched owner", "Description", "Kitchen", now));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using (var db = create())
        {
            var grant = await db.Memberships.SingleAsync(x => x.Id == membership.Id);
            grant.Revoke("operator", "Membership ended", now);
            db.ActiveGrants.Remove(await db.ActiveGrants.SingleAsync(x => x.MembershipId == membership.Id));
            await db.SaveChangesAsync();
        }
        await using (var db = create())
        {
            Assert.Empty(await db.ActiveGrants.Where(x => x.UserId == user.Id).ToListAsync());
            Assert.False((await db.Memberships.SingleAsync(x => x.Id == membership.Id)).IsActive(now));
            Assert.Equal(WorkOrderStatus.Triaged, (await db.WorkOrders.SingleAsync(x => x.Id == request.Id)).Status);
        }
        await VerifyProtectedIdentity(create);
        await MaintenanceContract.VerifyAsync(create);
        await NotificationContract.VerifyAsync(create);
        await KnowledgeContract.VerifyAsync(create);
        await BillingContract.VerifyAsync(create);
        await TenancyClosureContract.VerifyAsync(create);
        await AutopayContract.VerifyAsync(create);
        await AccessContract.VerifyAsync(create);
        await OperatorRecoveryContract.VerifyAsync(create);
        await MaintenanceQueueContract.VerifyAsync(create);
        await MaintenanceReportingContract.VerifyAsync(create);
        await RentReportingContract.VerifyAsync(create);
        await TelemetryPersistenceContract.VerifyAsync(create);
        await DatabaseReadinessContract.VerifyAsync(create);
        await PaymentAttemptContract.VerifyAsync(create);
        await SettlementPaymentContract.VerifyAsync(create);
        await CollectionAdmissionContract.VerifyAsync(create);
        await PaymentWebhookContract.VerifyAsync(create);
        await CollectionReconciliationContract.VerifyAsync(create);
        await FinancialExceptionContract.VerifyAsync(create);
        await CollectionWorkContract.VerifyAsync(create);
        await PaymentEventWorkContract.VerifyAsync(create);
        await CustomerProvisionContract.VerifyAsync(create);
        await LocalCreditContract.VerifyAsync(create);
        await CashMovementContract.VerifyAsync(create);
        await CashSourceRecoveryContract.VerifyAsync(create);
        await RefundDispositionContract.VerifyAsync(create);
        await CashAgreementContract.VerifyAsync(create);
        await InitialCashInventoryContract.VerifyAsync(create);
        await PayerCollectionContract.VerifyAsync(create);
    }

    private static async Task VerifyProtectedIdentity(Func<OfficeDbContext> create)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddScoped<OfficeDbContext>(_ => create());
        services.AddIdentityCore<OfficeUser>().AddRoles<IdentityRole>().AddEntityFrameworkStores<OfficeDbContext>().AddDefaultTokenProviders();
        services.Replace(ServiceDescriptor.Scoped<IUserStore<OfficeUser>, ProtectedUserStore>());
        await using var provider = services.BuildServiceProvider();
        string id; string code;
        using (var scope = provider.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
            var user = new OfficeUser { UserName = $"identity-{Guid.NewGuid():N}@example.test" };
            Assert.True((await users.CreateAsync(user, AccountTestHost.Password)).Succeeded); id = user.Id;
            Assert.True((await users.ResetAuthenticatorKeyAsync(user)).Succeeded);
            var key = await users.GetAuthenticatorKeyAsync(user);
            code = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 2))!.First();
            var values = await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().UserTokens.Where(x => x.UserId == id).Select(x => x.Value).ToListAsync();
            Assert.DoesNotContain(key, values); Assert.All(values, value => Assert.DoesNotContain(code, value));
        }
        using var first = provider.CreateScope(); using var second = provider.CreateScope();
        var a = first.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
        var b = second.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
        var original = (await a.FindByIdAsync(id))!; var stale = (await b.FindByIdAsync(id))!;
        Assert.True((await a.RedeemTwoFactorRecoveryCodeAsync(original, code)).Succeeded);
        Assert.False((await b.RedeemTwoFactorRecoveryCodeAsync(stale, code)).Succeeded);
        using var final = provider.CreateScope();
        var manager = final.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
        var saved = (await manager.FindByIdAsync(id))!;
        Assert.True(saved.MustReplaceRecoveryCodes); Assert.Equal(1, await manager.CountRecoveryCodesAsync(saved));
    }
}

public sealed class MySqlFactAttribute : FactAttribute
{
    public MySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OFFICE_TEST_MYSQL")))
            Skip = "Requires the isolated MySQL check: ./scripts/verify-mysql.sh";
    }
}
