using System.Text.Json;
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
foreach (var project in new[] { "chromium", "mobile" })
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
    fixtures[project] = new { tenantEmail = tenant.Email, managerEmail = manager.Email, password, key = await users.GetAuthenticatorKeyAsync(manager) };
}
var path = Path.Combine(directory, "accounts.json");
await File.WriteAllTextAsync(path, JsonSerializer.Serialize(fixtures));
if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
