using System.Net;
using System.Text.RegularExpressions;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AllenBuilding.IntegrationTests;

public sealed class AccountTestHost : WebApplicationFactory<Program>
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"office-accounts-{Guid.NewGuid():N}.db");
    public const string Password = "Synthetic-account-2026!";
    public TestClock Clock { get; } = new();
    public bool BillingPlanningEnabled { get; init; } = true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Billing:PlanningEnabled"] = BillingPlanningEnabled.ToString() }));
        builder.ConfigureServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock));
            services.RemoveAll<SqliteOfficeDbContext>();
            services.RemoveAll<DbContextOptions<SqliteOfficeDbContext>>();
            services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<SqliteOfficeDbContext>>();
            services.AddDbContext<SqliteOfficeDbContext>(options => options.UseSqlite($"Data Source={_path};Foreign Keys=True"));
        });
    }

    public async Task InitializeAsync()
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().Database.MigrateAsync();
    }

    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public async Task<(string UserId, Guid BuildingId)> SeedManagerAsync(bool enrolled = true)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
        var user = new OfficeUser { Email = "manager@example.test", UserName = "manager@example.test", EmailConfirmed = true };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        if (enrolled)
        {
            Assert.True((await users.ResetAuthenticatorKeyAsync(user)).Succeeded);
            Assert.True((await users.SetTwoFactorEnabledAsync(user, true)).Succeeded);
        }
        var building = new AllenBuilding.Domain.Tenancy.Building { Name = "Synthetic building" };
        db.Add(building);
        db.BuildingManagers.Add(new AllenBuilding.Domain.Tenancy.BuildingManager { BuildingId = building.Id, UserId = user.Id });
        await db.SaveChangesAsync();
        return (user.Id, building.Id);
    }

    public async Task<string> FactorCodeAsync(string userId)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
        var key = await users.GetAuthenticatorKeyAsync((await users.FindByIdAsync(userId))!);
        return Totp(key!);
    }

    public static async Task<HttpResponseMessage> PostAsync(HttpClient client, string formPage, string action, params (string Key, string Value)[] values)
    {
        var html = await client.GetStringAsync(formPage);
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        return await client.PostAsync(action, new FormUrlEncodedContent(values.Select(x => new KeyValuePair<string, string>(x.Key, x.Value))
            .Append(new("__RequestVerificationToken", WebUtility.HtmlDecode(token)))));
    }

    public async Task LoginManagerAsync(HttpClient client, string userId)
    {
        using var password = await PostAsync(client, "/Account/Login", "/Account/Login", ("Email", "manager@example.test"), ("Password", Password));
        Assert.Equal("/Account/Factor", password.Headers.Location?.OriginalString);
        using var factor = await PostAsync(client, "/Account/Factor", "/Account/Factor", ("Code", await FactorCodeAsync(userId)));
        Assert.Equal("/Portal", factor.Headers.Location?.OriginalString);
    }

    public static string Totp(string key)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>(); var buffer = 0; var bits = 0;
        foreach (var character in key)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(character); bits += 5;
            if (bits >= 8) { bits -= 8; bytes.Add((byte)(buffer >> bits)); }
        }
        var counter = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);
        var hash = System.Security.Cryptography.HMACSHA1.HashData(bytes.ToArray(), counter);
        var offset = hash[^1] & 15;
        var number = ((hash[offset] & 127) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (number % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix);
    }
}

public sealed class TestClock : TimeProvider
{
    public TimeSpan Offset { get; set; }
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Offset;
}
