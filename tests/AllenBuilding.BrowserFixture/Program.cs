using System.Text.Json;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

// An executable test fixture, never a web route or a production seed command.
var root = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "browser-fixture"));
var directory = Path.GetFullPath(args.Single());
if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
    throw new InvalidOperationException("Browser fixtures must be inside the ignored artifact directory.");
Directory.CreateDirectory(directory);
var database = Path.Combine(directory, "office.db");
if (File.Exists(database)) throw new InvalidOperationException("Refusing to overwrite an existing database.");
var services = new ServiceCollection(); services.AddLogging();
services.AddDbContext<SqliteOfficeDbContext>(options => options.UseSqlite($"Data Source={database};Foreign Keys=True"));
services.AddScoped<OfficeDbContext>(sp => sp.GetRequiredService<SqliteOfficeDbContext>());
services.AddDataProtection().SetApplicationName("AllenXeniaOffice.Build").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(directory, "keys")));
services.AddIdentityCore<OfficeUser>().AddRoles<IdentityRole>().AddEntityFrameworkStores<OfficeDbContext>().AddDefaultTokenProviders();
services.Replace(ServiceDescriptor.Scoped<IUserStore<OfficeUser>, ProtectedUserStore>());
await using var provider = services.BuildServiceProvider(); using var scope = provider.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
await db.Database.MigrateAsync();
const string password = "Synthetic-browser-2026!";
var fixtures = new Dictionary<string, object>();
foreach (var project in new[] { "chromium", "mobile", "webkit-mobile" })
{
    var building = new Building { Name = $"Browser test {project}" }; var suite = new Suite { BuildingId = building.Id, Name = "Synthetic office" };
    db.AddRange(building, suite); await db.SaveChangesAsync();
    var tenant = new OfficeUser { Email = $"tenant-{project}@example.test", UserName = $"tenant-{project}@example.test", EmailConfirmed = true };
    var manager = new OfficeUser { Email = $"manager-{project}@example.test", UserName = $"manager-{project}@example.test", EmailConfirmed = true };
    foreach (var user in new[] { tenant, manager })
        if (!(await users.CreateAsync(user, password)).Succeeded) throw new InvalidOperationException("Fixture account creation failed.");
    await users.ResetAuthenticatorKeyAsync(manager); await users.SetTwoFactorEnabledAsync(manager, true);
    var membership = Membership.Grant(suite.Id, tenant.Id, DateTimeOffset.UtcNow.AddDays(-1));
    db.AddRange(membership, new ActiveGrant { UserId = tenant.Id, SuiteId = suite.Id, MembershipId = membership.Id },
        new BuildingManager { UserId = manager.Id, BuildingId = building.Id }); await db.SaveChangesAsync();
    var oldMembership = Membership.Grant(suite.Id, tenant.Id, DateTimeOffset.UtcNow.AddYears(-1));
    oldMembership.Revoke(manager.Id, "Previous tenancy ended", DateTimeOffset.UtcNow.AddDays(-2));
    var account = RentAccount.Open(Guid.NewGuid(), oldMembership.Id);
    var binding = BillingCustomerBinding.Create(account.Id, "acct_browser_" + project.Replace('-', '_'), false, "cus_browser_" + project.Replace('-', '_'), DateTimeOffset.UtcNow);
    var debt = Obligation.Issue(account.Id, new Money(2500), "Previous tenancy charge", new DateOnly(2026, 10, 1), manager.Id, DateTimeOffset.UtcNow);
    var attempt = PaymentAttempt.Admit(Guid.NewGuid(), tenant.Id, binding, [new(debt.Id, account.Id, debt.Version, 2500)], DateTimeOffset.UtcNow);
    attempt.BeginDispatch(DateTimeOffset.UtcNow);
    var capture = SettlementPayment.Record(attempt, new(binding.ProviderAccount, false, binding.CustomerId, "cs_browser_" + project.Replace('-', '_'), "pi_browser_" + project.Replace('-', '_'), 2500, "USD"), DateTimeOffset.UtcNow);
    var movement = CashMovement.Record(capture, attempt, new(binding.ProviderAccount, false, account.Id, attempt.Id, binding.CustomerId, attempt.PaymentId!,
        "re_browser_" + project.Replace('-', '_'), "txn_browser_" + project.Replace('-', '_'), CashMovementKind.Refund, -500, "USD", DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
    db.AddRange(oldMembership, account, binding, debt, attempt, capture, movement, CashMovementReview.Open(movement),
        FinancialException.Open(attempt, FinancialFailure.ExternalCashMovement, "Paid", DateTimeOffset.UtcNow));
    await db.SaveChangesAsync();
    var paySuite = new Suite { BuildingId = building.Id, Name = "Payment test office" };
    var payMember = Membership.Grant(paySuite.Id, tenant.Id, DateTimeOffset.UtcNow.AddDays(-1));
    var payAccount = RentAccount.Open(Guid.NewGuid(), payMember.Id);
    var payBinding = BillingCustomerBinding.Create(payAccount.Id, "acct_browser_payments", false, "cus_pay_" + project.Replace('-', '_'), DateTimeOffset.UtcNow);
    var payDebt = Obligation.Issue(payAccount.Id, new Money(1000), "Current payment charge", new DateOnly(2026, 10, 1), manager.Id, DateTimeOffset.UtcNow);
    var paySmall = Obligation.Issue(payAccount.Id, new Money(1000), "Earlier small remainder", new DateOnly(2026, 9, 1), manager.Id, DateTimeOffset.UtcNow);
    var payCredit = ObligationCredit.RecordLocal(Guid.NewGuid(), manager.Id, new(paySmall.Id, payAccount.Id, paySmall.Version, 1000), 975, ObligationCreditKind.Credit, "Approved earlier credit", DateTimeOffset.UtcNow); paySmall.FinancialStateChanged();
    db.AddRange(paySuite, payMember, payAccount, payBinding, payDebt, paySmall, payCredit, new ActiveGrant { UserId = tenant.Id, SuiteId = paySuite.Id, MembershipId = payMember.Id }); await db.SaveChangesAsync();
    fixtures[project] = new { membershipId = membership.Id, paymentAccountId = payAccount.Id, tenantEmail = tenant.Email, managerEmail = manager.Email, password, key = await users.GetAuthenticatorKeyAsync(manager) };
}
var path = Path.Combine(directory, "accounts.json");
await File.WriteAllTextAsync(path, JsonSerializer.Serialize(fixtures));
if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
