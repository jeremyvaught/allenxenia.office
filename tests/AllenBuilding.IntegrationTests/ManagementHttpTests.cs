using System.Net;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AllenBuilding.IntegrationTests;

public sealed class ManagementHttpTests
{
    [Fact]
    public async Task Adding_a_suite_is_audited_unique_per_building_and_limited_to_managed_buildings()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (managerId, building) = await host.SeedManagerAsync();
        var foreign = new Building { Name = "Unmanaged building" };
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            db.Add(foreign);
            await db.SaveChangesAsync();
        }
        using var browser = host.Browser();
        await host.LoginManagerAsync(browser, managerId);

        using var created = await AccountTestHost.PostAsync(browser, "/Management", "/Management/AddSuite",
            ("BuildingId", building.ToString()), ("Name", "  Suite 210 "));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.Equal("/Management", created.Headers.Location?.OriginalString);
        var page = await browser.GetStringAsync("/Management");
        Assert.Contains("Suite Suite 210 added. You can now invite a tenant or grant access to this suite.", page);
        Assert.Contains("<option value=\"", page);
        Assert.Contains(">Suite 210</option>", page);
        Assert.Contains(">Synthetic building</option>", page);
        Assert.DoesNotContain("Unmanaged building", page);

        using var duplicate = await AccountTestHost.PostAsync(browser, "/Management", "/Management/AddSuite",
            ("BuildingId", building.ToString()), ("Name", "Suite 210"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var unmanaged = await AccountTestHost.PostAsync(browser, "/Management", "/Management/AddSuite",
            ("BuildingId", foreign.Id.ToString()), ("Name", "Suite 999"));
        Assert.Equal(HttpStatusCode.BadRequest, unmanaged.StatusCode);
        using var blank = await AccountTestHost.PostAsync(browser, "/Management", "/Management/AddSuite",
            ("BuildingId", building.ToString()), ("Name", "   "));
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);

        using var check = host.Services.CreateScope();
        var saved = check.ServiceProvider.GetRequiredService<OfficeDbContext>();
        var suite = await saved.Suites.AsNoTracking().SingleAsync(x => x.BuildingId == building);
        Assert.Equal("Suite 210", suite.Name);
        Assert.False(await saved.Suites.AnyAsync(x => x.BuildingId == foreign.Id));
        var audit = await saved.AccountAudits.AsNoTracking().SingleAsync(x => x.Action == "SuiteCreated");
        Assert.Equal(managerId, audit.ActorId);
        Assert.Equal(suite.Id.ToString(), audit.SubjectId);
    }

    [Fact]
    public async Task Revoking_access_rejects_invalid_unknown_foreign_and_stale_requests_then_revokes_once()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (managerId, building) = await host.SeedManagerAsync();
        var suite = await SeedSuiteAsync(host, building, "Shared office");
        var membership = await SeedTenantAsync(host, "tenant@example.test", suite);
        var foreignSuite = await SeedSuiteAsync(host, await SeedBuildingAsync(host, "Unmanaged building"), "Foreign suite");
        var foreign = await SeedTenantAsync(host, "foreign@example.test", foreignSuite);
        var version = await MembershipVersionAsync(host, membership);
        using var browser = host.Browser();
        await host.LoginManagerAsync(browser, managerId);
        Assert.Matches(@"tenant@example\.test</p>\s*<p[^>]*>Suite Shared office<", await browser.GetStringAsync("/Management"));

        using var missingReason = await Revoke(membership, version, "");
        Assert.Equal(HttpStatusCode.BadRequest, missingReason.StatusCode);
        using var emptyVersion = await Revoke(membership, Guid.Empty, "Lease ended");
        Assert.Equal(HttpStatusCode.BadRequest, emptyVersion.StatusCode);
        using var unknown = await Revoke(Guid.NewGuid(), version, "Lease ended");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var unmanaged = await Revoke(foreign, await MembershipVersionAsync(host, foreign), "Lease ended");
        Assert.Equal(HttpStatusCode.NotFound, unmanaged.StatusCode);
        using var stale = await Revoke(membership, Guid.NewGuid(), "Lease ended");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using (var scope = host.Services.CreateScope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().Memberships.AnyAsync(x => x.RevokedAt != null));

        using var revoked = await Revoke(membership, version, "  Lease ended ");
        Assert.Equal(HttpStatusCode.Redirect, revoked.StatusCode);
        Assert.Equal("/Management", revoked.Headers.Location?.OriginalString);
        var page = await browser.GetStringAsync("/Management");
        Assert.Contains("Tenant access revoked. Existing records are retained. Billing arrangements are unchanged.", page);
        Assert.DoesNotContain("tenant@example.test", page);
        using var repeated = await Revoke(membership, version, "Lease ended");
        Assert.Equal(HttpStatusCode.Redirect, repeated.StatusCode);

        using var check = host.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<OfficeDbContext>();
        var saved = await db.Memberships.AsNoTracking().SingleAsync(x => x.Id == membership);
        Assert.NotNull(saved.RevokedAt);
        Assert.Equal(managerId, saved.RevokedBy);
        Assert.Equal("Lease ended", saved.RevocationReason);
        Assert.Null((await db.Memberships.AsNoTracking().SingleAsync(x => x.Id == foreign)).RevokedAt);
        Assert.False(await db.ActiveGrants.AnyAsync(x => x.MembershipId == membership));
        Assert.True(await db.ActiveGrants.AnyAsync(x => x.MembershipId == foreign));
        Assert.Single(await db.AccountAudits.AsNoTracking().Where(x => x.Action == "AccessRevoked").ToListAsync());

        Task<HttpResponseMessage> Revoke(Guid id, Guid expected, string reason) => AccountTestHost.PostAsync(browser, "/Management", "/Management/Revoke",
            ("MembershipId", id.ToString()), ("Version", expected.ToString()), ("Reason", reason));
    }

    [Fact]
    public async Task Granting_an_existing_account_requires_a_confirmed_account_and_replays_only_the_same_operation()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (managerId, building) = await host.SeedManagerAsync();
        var suite = await SeedSuiteAsync(host, building, "Second floor");
        string returningId;
        using (var scope = host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
            var returning = new OfficeUser { Email = "returning@example.test", UserName = "returning@example.test", EmailConfirmed = true };
            Assert.True((await users.CreateAsync(returning, AccountTestHost.Password)).Succeeded);
            returningId = returning.Id;
            Assert.True((await users.CreateAsync(new OfficeUser { Email = "pending@example.test", UserName = "pending@example.test" })).Succeeded);
        }
        using var browser = host.Browser();
        await host.LoginManagerAsync(browser, managerId);
        var operation = Guid.NewGuid();

        using var missingReason = await Grant(operation, suite, "returning@example.test", "");
        Assert.Equal(HttpStatusCode.BadRequest, missingReason.StatusCode);
        using var emptyOperation = await Grant(Guid.Empty, suite, "returning@example.test", "Second lease");
        Assert.Equal(HttpStatusCode.BadRequest, emptyOperation.StatusCode);
        using var unknownSuite = await Grant(operation, Guid.NewGuid(), "returning@example.test", "Second lease");
        Assert.Equal(HttpStatusCode.NotFound, unknownSuite.StatusCode);
        using var unknownAccount = await Grant(operation, suite, "nobody@example.test", "Second lease");
        Assert.Equal(HttpStatusCode.NotFound, unknownAccount.StatusCode);
        using var unconfirmed = await Grant(operation, suite, "pending@example.test", "Second lease");
        Assert.Equal(HttpStatusCode.NotFound, unconfirmed.StatusCode);

        using var granted = await Grant(operation, suite, "returning@example.test", "Second lease");
        Assert.Equal(HttpStatusCode.Redirect, granted.StatusCode);
        Assert.Equal("/Management", granted.Headers.Location?.OriginalString);
        var page = await browser.GetStringAsync("/Management");
        Assert.Contains("Suite access granted to the existing account. Previous memberships and passwords are unchanged.", page);
        Assert.Matches(@"returning@example\.test</p>\s*<p[^>]*>Suite Second floor<", page);
        using var replay = await Grant(operation, suite, "returning@example.test", "Second lease");
        Assert.Equal(HttpStatusCode.Redirect, replay.StatusCode);
        using var changedReplay = await Grant(operation, suite, "returning@example.test", "Different reason");
        Assert.Equal(HttpStatusCode.Conflict, changedReplay.StatusCode);
        using var duplicate = await Grant(Guid.NewGuid(), suite, "returning@example.test", "Second lease");
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        using var check = host.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<OfficeDbContext>();
        var membership = await db.Memberships.AsNoTracking().SingleAsync();
        Assert.Equal((suite, returningId), (membership.SuiteId, membership.UserId));
        Assert.True(await db.ActiveGrants.AnyAsync(x => x.MembershipId == membership.Id));
        Assert.Equal(membership.Id, (await db.AccessReceipts.AsNoTracking().SingleAsync()).MembershipId);
        var audit = await db.AccountAudits.AsNoTracking().SingleAsync(x => x.Action == "ExistingAccountGranted");
        Assert.Equal((managerId, membership.Id.ToString("D")), (audit.ActorId, audit.SubjectId));
        Assert.Single(await db.AccountMessages.AsNoTracking().Where(x => x.UserId == returningId && x.Kind == "SecurityNotice").ToListAsync());

        Task<HttpResponseMessage> Grant(Guid id, Guid suiteId, string email, string reason) => AccountTestHost.PostAsync(browser, "/Management", "/Management/Grant",
            ("OperationId", id.ToString()), ("SuiteId", suiteId.ToString()), ("Email", email), ("Reason", reason));
    }

    [Fact]
    public async Task Invitations_reject_invalid_or_existing_accounts_and_change_only_with_the_current_version()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (managerId, building) = await host.SeedManagerAsync();
        var suite = await SeedSuiteAsync(host, building, "Ground floor");
        using var browser = host.Browser();
        await host.LoginManagerAsync(browser, managerId);

        using var invalidEmail = await Invite(suite, "not-an-email");
        Assert.Equal(HttpStatusCode.BadRequest, invalidEmail.StatusCode);
        using var existing = await Invite(suite, "manager@example.test");
        Assert.Equal(HttpStatusCode.Conflict, existing.StatusCode);
        Assert.Contains("Unable to create this invitation. Check the suite and account, then try again.", await existing.Content.ReadAsStringAsync());
        using var unknownSuite = await Invite(Guid.NewGuid(), "invited@example.test");
        Assert.Equal(HttpStatusCode.Conflict, unknownSuite.StatusCode);
        using var issued = await Invite(suite, "invited@example.test");
        Assert.Equal(HttpStatusCode.Redirect, issued.StatusCode);
        var page = await browser.GetStringAsync("/Management");
        Assert.Contains("Invitation created and queued to send.", page);
        Assert.Contains("invited@example.test", page);
        Assert.Contains("Ground floor · Expires", page);
        Assert.Contains(">Pending</span>", page);

        var (id, version) = await InvitationAsync(host);
        using var missingReason = await Change(id, version, true, "");
        Assert.Equal(HttpStatusCode.BadRequest, missingReason.StatusCode);
        using var emptyVersion = await Change(id, Guid.Empty, true, "Sent to the wrong person");
        Assert.Equal(HttpStatusCode.BadRequest, emptyVersion.StatusCode);
        using var unknown = await Change(Guid.NewGuid(), version, true, "Sent to the wrong person");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var stale = await Change(id, Guid.NewGuid(), true, "Sent to the wrong person");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(version, (await InvitationAsync(host)).Version);

        using var revoked = await Change(id, version, true, "Sent to the wrong person");
        Assert.Equal(HttpStatusCode.Redirect, revoked.StatusCode);
        page = await browser.GetStringAsync("/Management");
        Assert.Contains("Invitation revoked. Its link cannot activate access.", page);
        Assert.Contains(">Revoked</span>", page);
        Assert.DoesNotContain("Revoke invitation</button>", page);
        var (_, revokedVersion) = await InvitationAsync(host);
        using var revokedAgain = await Change(id, revokedVersion, true, "Sent to the wrong person");
        Assert.Equal(HttpStatusCode.Conflict, revokedAgain.StatusCode);
        using var replayedOldVersion = await Change(id, version, false, "Confirmed recipient");
        Assert.Equal(HttpStatusCode.Conflict, replayedOldVersion.StatusCode);

        using var reissued = await Change(id, revokedVersion, false, "Confirmed recipient");
        Assert.Equal(HttpStatusCode.Redirect, reissued.StatusCode);
        page = await browser.GetStringAsync("/Management");
        Assert.Contains("Invitation reissued for seven days. Previous links are invalid; the new link is queued for delivery.", page);
        Assert.Contains(">Pending</span>", page);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            var invitation = await db.Invitations.SingleAsync();
            Assert.Null(invitation.RevokedAt);
            Assert.Equal(invitation.Version, invitation.TokenVersion);
            Assert.NotEqual(revokedVersion, invitation.Version);
            Assert.Equal(2, await db.AccountMessages.CountAsync(x => x.UserId == invitation.UserId && x.Kind == "Invitation"));
            var actions = await db.AccountAudits.AsNoTracking().Where(x => x.SubjectId == id.ToString("D") || x.SubjectId == id.ToString())
                .Select(x => x.Action).ToListAsync();
            Assert.Equal(["InvitationIssued", "InvitationReissued", "InvitationRevoked"], actions.Order());
            invitation.ExpiresAt = host.Clock.GetUtcNow().AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        Assert.Contains(">Expired</span>", await browser.GetStringAsync("/Management"));

        Task<HttpResponseMessage> Invite(Guid suiteId, string email) => AccountTestHost.PostAsync(browser, "/Management", "/Management/Invite",
            ("SuiteId", suiteId.ToString()), ("Email", email));
        Task<HttpResponseMessage> Change(Guid invitation, Guid expected, bool revoke, string reason) => AccountTestHost.PostAsync(browser, "/Management",
            "/Management/ChangeInvitation", ("InvitationId", invitation.ToString()), ("Version", expected.ToString()), ("Revoke", revoke.ToString()), ("Reason", reason));
    }

    [Fact]
    public async Task Tenants_are_denied_management_contractor_and_article_management_pages_and_actions()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (_, building) = await host.SeedManagerAsync();
        var suite = await SeedSuiteAsync(host, building, "Shared office");
        var membership = await SeedTenantAsync(host, "tenant@example.test", suite);
        using var tenant = host.Browser();
        using (var login = await AccountTestHost.PostAsync(tenant, "/Account/Login", "/Account/Login",
            ("Email", "tenant@example.test"), ("Password", AccountTestHost.Password)))
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        foreach (var path in new[] { "/Management", "/Contractors", "/Contractors/Create", "/Knowledge/Manage", "/Knowledge/Create" })
        {
            using var denied = await tenant.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
            Assert.StartsWith("http://localhost/Account/Denied", denied.Headers.Location?.OriginalString);
        }
        using var revoke = await AccountTestHost.PostAsync(tenant, "/Portal", "/Management/Revoke",
            ("MembershipId", membership.ToString()), ("Version", (await MembershipVersionAsync(host, membership)).ToString()), ("Reason", "Self revoke"));
        Assert.StartsWith("http://localhost/Account/Denied", revoke.Headers.Location?.OriginalString);
        using var addSuite = await AccountTestHost.PostAsync(tenant, "/Portal", "/Management/AddSuite", ("BuildingId", building.ToString()), ("Name", "Tenant suite"));
        Assert.StartsWith("http://localhost/Account/Denied", addSuite.Headers.Location?.OriginalString);

        using var check = host.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<OfficeDbContext>();
        Assert.Null((await db.Memberships.AsNoTracking().SingleAsync()).RevokedAt);
        Assert.Single(await db.Suites.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Suite_names_longer_than_the_limit_are_rejected_without_saving()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (managerId, building) = await host.SeedManagerAsync();
        using var browser = host.Browser();
        await host.LoginManagerAsync(browser, managerId);
        using var tooLong = await AccountTestHost.PostAsync(browser, "/Management", "/Management/AddSuite",
            ("BuildingId", building.ToString()), ("Name", new string('S', 81)));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        using var scope = host.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().Suites.AnyAsync());
    }

    internal static async Task<Guid> SeedBuildingAsync(AccountTestHost host, string name)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
        var building = new Building { Name = name };
        db.Add(building);
        await db.SaveChangesAsync();
        return building.Id;
    }

    internal static async Task<Guid> SeedSuiteAsync(AccountTestHost host, Guid building, string name)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
        var suite = new Suite { BuildingId = building, Name = name };
        db.Suites.Add(suite);
        await db.SaveChangesAsync();
        return suite.Id;
    }

    internal static async Task<Guid> SeedTenantAsync(AccountTestHost host, string email, Guid suite)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
        var user = new OfficeUser { Email = email, UserName = email, EmailConfirmed = true };
        Assert.True((await users.CreateAsync(user, AccountTestHost.Password)).Succeeded);
        var membership = Membership.Grant(suite, user.Id, DateTimeOffset.UtcNow.AddDays(-1));
        db.AddRange(membership, new ActiveGrant { UserId = user.Id, SuiteId = suite, MembershipId = membership.Id });
        await db.SaveChangesAsync();
        return membership.Id;
    }

    private static async Task<Guid> MembershipVersionAsync(AccountTestHost host, Guid membership)
    {
        using var scope = host.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().Memberships.AsNoTracking().SingleAsync(x => x.Id == membership)).Version;
    }

    private static async Task<(Guid Id, Guid Version)> InvitationAsync(AccountTestHost host)
    {
        using var scope = host.Services.CreateScope();
        var invitation = await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().Invitations.AsNoTracking().SingleAsync();
        return (invitation.Id, invitation.Version);
    }
}
