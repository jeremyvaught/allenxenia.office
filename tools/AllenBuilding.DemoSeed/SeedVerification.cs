using AllenBuilding.Application.Access;
using AllenBuilding.Application.Billing;
using AllenBuilding.Application.Maintenance;
using AllenBuilding.Application.Reporting;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Knowledge;
using AllenBuilding.Domain.Maintenance;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AllenBuilding.DemoSeed;

public sealed record SeedCheck(string Name, int Actual, int Expected, bool Minimum = false)
{
    public bool Ok => Minimum ? Actual >= Expected : Actual == Expected;
}

public sealed record SeedReport(string Directory, IReadOnlyList<SeedCheck> Checks)
{
    public IReadOnlyList<SeedCheck> Failures => Checks.Where(x => !x.Ok).ToArray();
    public int this[string name] => Checks.Single(x => x.Name == name).Actual;
}

// Counts what was written and compares it with the demo specification, so a model change that
// quietly alters the seed is caught here and not on recording day.
internal static class SeedVerification
{
    public static async Task<SeedReport> CheckAsync(IServiceProvider sp, SeedActor actor, DateTimeOffset now, string directory,
        IReadOnlyList<BillingEvent> billingPlan)
    {
        var db = sp.GetRequiredService<OfficeDbContext>();
        var checks = new List<SeedCheck>();
        void Exactly(string name, int actual, int expected) => checks.Add(new(name, actual, expected));
        void AtLeast(string name, int actual, int expected) => checks.Add(new(name, actual, expected, Minimum: true));

        var building = await db.Buildings.AsNoTracking().ToListAsync();
        Exactly("Buildings named Allen Building", building.Count(x => x.Name == DemoContent.Building), 1);
        Exactly("Buildings", building.Count, 1);
        var suites = await db.Suites.AsNoTracking().ToListAsync();
        Exactly("Suites", suites.Count, 7);

        var managers = await db.BuildingManagers.AsNoTracking().Select(x => x.UserId).ToListAsync();
        var users = await db.Users.AsNoTracking().ToListAsync();
        Exactly("Managers", managers.Count, 2);
        Exactly("Managers with an authenticator enrolled", users.Count(x => managers.Contains(x.Id) && x.TwoFactorEnabled), 2);
        Exactly("Accounts outside example.test", users.Count(x => x.Email?.EndsWith("@example.test", StringComparison.Ordinal) != true), 0);

        Exactly("Managers shown by name", users.Count(x => managers.Contains(x.Id) &&
            new[] { DemoContent.Jordan.Name, DemoContent.Casey.Name }.Contains(x.DisplayName)), 2);

        var memberships = await db.Memberships.AsNoTracking().ToListAsync();
        var grants = await db.ActiveGrants.AsNoTracking().Select(x => x.MembershipId).ToListAsync();
        var active = memberships.Where(x => x.IsActive(now) && grants.Contains(x.Id)).ToList();
        Exactly("Tenants with access", active.Count, 7);
        Exactly("Suites with exactly one tenant", active.GroupBy(x => x.SuiteId).Count(x => x.Count() == 1), 7);
        Exactly("Tenants with access who have given a name", active.Count(x =>
            AllenBuilding.Infrastructure.Identity.PersonName.Of(users.Single(u => u.Id == x.UserId).DisplayName) is not null), 7);
        Exactly("Name changes recorded for managers", await db.AccountAudits.CountAsync(x => x.Action == "DisplayNameChanged"), 2);
        Exactly("Revoked memberships", memberships.Count(x => x.RevokedAt is not null), 1);
        var invitations = await db.Invitations.AsNoTracking().Where(x => x.AcceptedAt == null).ToListAsync();
        Exactly("Invitations pending", invitations.Count(x => x.RevokedAt is null && x.ExpiresAt > now), 1);
        Exactly("Invitations expired", invitations.Count(x => x.RevokedAt is null && x.ExpiresAt <= now), 1);

        var contractors = await db.Contractors.AsNoTracking().ToListAsync();
        Exactly("Contractors active", contractors.Count(x => x.Active), 3);
        Exactly("Contractors inactive", contractors.Count(x => !x.Active), 1);

        var orders = await db.WorkOrders.AsNoTracking().ToListAsync();
        var history = await db.WorkOrderHistory.AsNoTracking().ToListAsync();
        var byOrder = history.ToLookup(x => x.WorkOrderId);
        int Status(WorkOrderStatus status) => orders.Count(x => x.Status == status);
        bool Open(WorkOrder order) => order.Status is not (WorkOrderStatus.Closed or WorkOrderStatus.Cancelled);
        var reopened = orders.Where(x => byOrder[x.Id].Any(h => h.Action == "Reopen")).ToList();
        Exactly("Requests", orders.Count, 30);
        Exactly("Requests submitted and untouched", orders.Count(x => x.Status == WorkOrderStatus.Submitted && !reopened.Contains(x)), 4);
        Exactly("Requests reopened and back in submitted", reopened.Count(x => x.Status == WorkOrderStatus.Submitted), 1);
        Exactly("Requests triaged", Status(WorkOrderStatus.Triaged), 3);
        Exactly("Requests triaged with an assignment", orders.Count(x => x.Status == WorkOrderStatus.Triaged &&
            (x.AssignedManagerId is not null || x.AssignedContractorId is not null)), 0);
        Exactly("Requests assigned", Status(WorkOrderStatus.Assigned), 4);
        AtLeast("Requests assigned to a manager", orders.Count(x => x.Status == WorkOrderStatus.Assigned && x.AssignedManagerId is not null), 1);
        AtLeast("Requests assigned to a contractor", orders.Count(x => x.Status == WorkOrderStatus.Assigned && x.AssignedContractorId is not null), 1);
        Exactly("Requests in progress", Status(WorkOrderStatus.InProgress), 4);
        Exactly("Requests completed, waiting to verify", Status(WorkOrderStatus.Completed), 3);
        Exactly("Requests closed", Status(WorkOrderStatus.Closed), 9);
        Exactly("Requests cancelled", Status(WorkOrderStatus.Cancelled), 2);
        Exactly("Cancelled requests without a reason", orders.Count(x => x.Status == WorkOrderStatus.Cancelled &&
            !byOrder[x.Id].Any(h => h.Action == "Cancel" && !string.IsNullOrWhiteSpace(h.Note))), 0);
        Exactly("Closed requests without resolution text or a day between completion and closing", orders.Count(x =>
            x.Status == WorkOrderStatus.Closed && (string.IsNullOrWhiteSpace(x.Resolution) ||
                byOrder[x.Id].Where(h => h.Action == "Close").Max(h => h.OccurredAt) -
                byOrder[x.Id].Where(h => h.Action == "Complete").Max(h => h.OccurredAt) < TimeSpan.FromDays(1))), 0);
        Exactly("Trades in use", orders.Where(x => x.Trade is not null).Select(x => x.Trade).Distinct().Count(), Enum.GetValues<Trade>().Length);
        Exactly("Priorities in use", orders.Where(x => x.Priority is not null).Select(x => x.Priority).Distinct().Count(), Enum.GetValues<Priority>().Length);
        AtLeast("Open urgent requests", orders.Count(x => Open(x) && x.Priority == Priority.Urgent), 2);
        AtLeast("Open requests older than 21 days", orders.Count(x => Open(x) && x.CreatedAt <= now.AddDays(-21)), 2);
        Exactly("Requests older than 60 days", orders.Count(x => x.CreatedAt < now.AddDays(-60)), 0);
        AtLeast("Requests with a tenant comment and a manager reply", orders.Count(x =>
            byOrder[x.Id].Any(h => h.Action == "Comment" && h.ActorId == x.RequesterUserId) &&
            byOrder[x.Id].Any(h => h.Action == "Comment" && h.ActorId != x.RequesterUserId && h.Visibility == HistoryVisibility.TenantVisible)), 8);
        AtLeast("Requests with an internal manager note", orders.Count(x => byOrder[x.Id].Any(h => h.Visibility == HistoryVisibility.Internal)), 4);
        Exactly("History rows without a receipt", history.Count - await db.MaintenanceReceipts.CountAsync(), 0);

        var revoked = memberships.Where(x => x.RevokedAt is not null).Select(x => x.Id).ToList();
        Exactly("Closed requests from the former tenant", orders.Count(x => revoked.Contains(x.RequesterMembershipId) && x.Status == WorkOrderStatus.Closed), 2);
        Exactly("Other requests from the former tenant", orders.Count(x => revoked.Contains(x.RequesterMembershipId) && x.Status != WorkOrderStatus.Closed), 0);
        var inactive = contractors.Where(x => !x.Active).Select(x => (Guid?)x.Id).ToList();
        Exactly("Closed requests still showing the inactive contractor", orders.Count(x => inactive.Contains(x.AssignedContractorId) && x.Status == WorkOrderStatus.Closed), 1);
        Exactly("Other requests with the inactive contractor", orders.Count(x => inactive.Contains(x.AssignedContractorId) && x.Status != WorkOrderStatus.Closed), 0);

        // The three reservations for on-camera steps.
        Guid Suite(string name) => suites.Single(x => x.Name == name).Id;
        var newest = orders.OrderByDescending(x => x.CreatedAt).First();
        Exactly("Newest request is an untouched one from suite 201",
            newest.SuiteId == Suite("201") && newest.Status == WorkOrderStatus.Submitted && byOrder[newest.Id].Count() == 1 ? 1 : 0, 1);
        Exactly("Assigned requests with no comments, for the stale-page step",
            orders.Count(x => x.Status == WorkOrderStatus.Assigned && !byOrder[x.Id].Any(h => h.Action == "Comment")) > 0 ? 1 : 0, 1);
        Exactly("Open requests from suite 302", orders.Count(x => x.SuiteId == Suite("302") && Open(x)), 1);

        // Each tenant, reading through the same action as the portal, sees only their own suite.
        var maintenance = sp.GetRequiredService<MaintenanceActions>();
        var leaks = 0;
        var visible = 0;
        foreach (var membership in active)
        {
            actor.Current = new Actor(membership.UserId, new HashSet<Guid>(), false, false);
            var suite = suites.Single(x => x.Id == membership.SuiteId).Name;
            var own = orders.Where(x => x.RequesterMembershipId == membership.Id).Select(x => x.Id).ToHashSet();
            var seen = await maintenance.ListAsync(management: false);
            visible += seen.Count;
            leaks += seen.Count(x => x.Suite != suite || !own.Contains(x.Id)) + (seen.Count == own.Count ? 0 : 1);
        }
        actor.Current = null;
        Exactly("Requests visible to current tenants", visible, 28);
        Exactly("Tenants who see a request that is not their own", leaks, 0);

        var articles = await db.HelpArticles.AsNoTracking().ToListAsync();
        var published = articles.Where(x => x.State == PublicationState.Published).Select(x => x.PublishedRevisionId).ToList();
        var audiences = await db.HelpRevisions.AsNoTracking().Where(x => published.Contains(x.Id)).Select(x => x.Audience).ToListAsync();
        Exactly("Help articles published", published.Count, 6);
        Exactly("Help articles in draft", articles.Count(x => x.State == PublicationState.Draft), 1);
        AtLeast("Published articles for the public", audiences.Count(x => x == ArticleAudience.Public), 1);
        AtLeast("Published articles for tenants only", audiences.Count(x => x == ArticleAudience.ActiveTenant), 1);

        var messages = await db.AccountMessages.AsNoTracking().ToListAsync();
        var updates = messages.Where(x => x.Kind == "MaintenanceUpdate" && x.State == MessageState.Delivered).ToList();
        AtLeast("Emails delivered", messages.Count(x => x.State == MessageState.Delivered), 60);
        AtLeast("Weeks spanned by delivered maintenance updates",
            updates.Count == 0 ? 0 : (int)((updates.Max(x => x.CreatedAt) - updates.Min(x => x.CreatedAt)).TotalDays / 7), 4);
        Exactly("Emails waiting to send", messages.Count(x => x.State == MessageState.Queued), 2);
        Exactly("Emails failed, with a retry available",
            messages.Count(x => x.State == MessageState.NeedsReview && x.Kind != "PasswordReset" && x.BuildingId is not null), 1);
        Exactly("Emails in any other state",
            messages.Count(x => x.State is not (MessageState.Delivered or MessageState.Queued or MessageState.NeedsReview)), 0);

        // Rent planning is shown; payment collection is not. Only the planning tables may hold rows.
        var planning = new[] { typeof(RentAccount), typeof(RentAgreement), typeof(BillingPeriod), typeof(Obligation), typeof(ObligationCredit), typeof(BillingReceipt) };
        var collection = 0;
        await db.Database.OpenConnectionAsync();
        foreach (var entity in db.Model.GetEntityTypes().Where(x => x.ClrType.Namespace == "AllenBuilding.Domain.Billing" && !planning.Contains(x.ClrType)))
        {
            var table = entity.GetTableName();
            if (table is null)
                continue;
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
            collection += Convert.ToInt32(await command.ExecuteScalarAsync());
        }
        await db.Database.CloseConnectionAsync();
        Exactly("Payment, autopay, provider and financial-exception rows", collection, 0);

        // Billing, read as a manager through the action behind the billing pages.
        var manager = new Actor(managers[0], building.Select(x => x.Id).ToHashSet(), true, true);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(building[0].TimeZoneId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        actor.Current = manager;
        var billing = sp.GetRequiredService<BillingActions>();
        var accounts = await billing.ReadAsync(management: true);
        var charges = accounts.SelectMany(x => x.Obligations).ToList();
        Exactly("Rent accounts", accounts.Count, 7);
        Exactly("Rent accounts for a current tenant", accounts.Count(x => active.Any(m => m.Id == x.MembershipId)), 7);
        Exactly("Rent accounts with rent terms", accounts.Count(x => x.Terms.Count == 1), 7);
        Exactly("Monthly rent charges", charges.Count(x => x.Kind == ChargeKind.BaseRent), billingPlan.Count(x => x.Step == BillingStep.Rent));
        AtLeast("Monthly rent charges, at least", charges.Count(x => x.Kind == ChargeKind.BaseRent), 12);
        Exactly("One-time charges", charges.Count(x => x.Kind == ChargeKind.OneTime), 3);
        Exactly("Credits recorded by a manager", charges.Sum(x => x.Credits?.Count ?? 0), billingPlan.Count(x => x.Step == BillingStep.Credit));
        Exactly("Charges dated in the future or before the demo window", await db.Obligations.CountAsync() -
            (await db.Obligations.AsNoTracking().Select(x => x.IssuedAt).ToListAsync()).Count(x => x < now && x >= now.AddDays(-62)), 0);
        var behind = accounts.Count(x => x.OverdueMinorUnits(today) > 0);
        AtLeast("Tenants with an overdue balance", behind, 1);
        Exactly("Tenants with an overdue balance, at most two", Math.Min(behind, 2), behind);
        AtLeast("Tenants with nothing overdue", accounts.Count - behind, 5);
        AtLeast("Tenants who owe nothing at all", accounts.Count(x => x.BalanceMinorUnits == 0), 4);
        AtLeast("Charges owed but not yet due", charges.Count(x => x.BalanceMinorUnits > 0 && x.DueOn >= today), 1);
        var balances = await sp.GetRequiredService<ManagementReportQueries>().RentBalancesAsync(new());
        Exactly("Rent report total matches the account pages", balances is not null &&
            balances.Totals.ConfirmedMinor == accounts.Sum(x => x.BalanceMinorUnits) && balances.Totals.OverdueMinor == accounts.Sum(x => x.OverdueMinorUnits(today)) &&
            balances.Totals.ConfirmedMinor > 0 && balances.Totals.HeldMinor == 0 ? 1 : 0, 1);
        // Each tenant, through the same action as their billing page, sees their own suite's account and nothing else.
        var strangers = 0;
        foreach (var membership in active)
        {
            actor.Current = new Actor(membership.UserId, new HashSet<Guid>(), false, false);
            var own = await billing.ReadAsync(management: false);
            if (own.Count != 1 || own[0].MembershipId != membership.Id || own[0].Obligations.Any(x => x.Credits?.Any(c => c.Reason is not null) == true))
                strangers++;
        }
        actor.Current = null;
        Exactly("Tenants who see billing that is not their own, or a credit reason", strangers, 0);

        // What the manager dashboard tiles will show, read through the action the dashboard uses.
        var summary = await sp.GetRequiredService<BuildingAccessActions>().ManagerSummaryAsync(manager, CancellationToken.None);
        Exactly("Dashboard: new requests to triage", summary.AwaitingTriage, 5);
        Exactly("Dashboard: finished, waiting to verify", summary.AwaitingVerification, 3);
        Exactly("Dashboard: invitations not yet accepted", summary.PendingInvitations, 1);
        return new(directory, checks);
    }
}
