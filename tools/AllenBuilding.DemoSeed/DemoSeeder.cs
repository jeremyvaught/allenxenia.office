using System.Text.Json;
using AllenBuilding.Application.Access;
using AllenBuilding.Application.Knowledge;
using AllenBuilding.Application.Maintenance;
using AllenBuilding.Application.Notifications;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Notifications;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AllenBuilding.DemoSeed;

// Builds the demonstration database by replaying two months of building use through the same
// application actions and identity services the web app calls, with the clock set to each moment.
public sealed class DemoSeeder
{
    private const string Solution = "AllenXeniaOffice.slnx";
    private readonly SeedClock _clock = new();
    private readonly SeedActor _actor = new();
    private readonly SeedTransport _transport = new();
    private readonly List<(DateTimeOffset At, int Order, Mail Mail, Func<IServiceProvider, Task> Run)> _timeline = [];
    private readonly Dictionary<string, string> _users = [];
    private readonly Dictionary<string, Guid> _suites = [];
    private readonly Dictionary<string, Guid> _contractors = [];
    private readonly Dictionary<string, Guid> _invitations = [];
    private readonly Dictionary<string, Guid> _memberships = [];
    private readonly DateTimeOffset _now;
    private readonly TimeZoneInfo _zone;
    private Guid _building;

    private DemoSeeder(DateTimeOffset now, TimeZoneInfo zone) => (_now, _zone) = (now, zone);

    public static async Task<SeedReport> RunAsync(string directory)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var demo = Directory.GetParent(directory);
        var repository = demo?.Parent?.Parent;
        if (demo?.Name != "demo" || demo.Parent?.Name != "artifacts" || repository is null ||
            !File.Exists(Path.Combine(repository.FullName, Solution)))
            throw new InvalidOperationException("Demo data must be written to artifacts/demo/<name> inside this repository.");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var database = Path.Combine(directory, "office.db");
        if (File.Exists(database))
            throw new InvalidOperationException("Refusing to overwrite an existing database. Choose a new directory name.");

        // Whole seconds, so stored times compare cleanly with the claims the web app issues.
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var seeder = new DemoSeeder(now, TimeZoneInfo.FindSystemTimeZoneById(new Building().TimeZoneId));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(seeder._clock);
        services.AddSingleton<ICurrentActor>(seeder._actor);
        services.AddSingleton<IEmailTransport>(seeder._transport);
        services.AddDbContext<SqliteOfficeDbContext>(options => options.UseSqlite($"Data Source={database};Foreign Keys=True"));
        services.AddScoped<OfficeDbContext>(sp => sp.GetRequiredService<SqliteOfficeDbContext>());
        // The web app reads authenticator keys and queued messages with these keys, under this application name.
        services.AddDataProtection().SetApplicationName("AllenXeniaOffice.Build")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(directory, "keys")));
        services.AddIdentityCore<OfficeUser>(options =>
        {
            options.Password.RequiredLength = 12;
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;
            options.User.RequireUniqueEmail = true;
        }).AddRoles<IdentityRole>().AddEntityFrameworkStores<OfficeDbContext>().AddDefaultTokenProviders()
            .AddTokenProvider<InvitationTokenProvider>("Invitation");
        services.Configure<InvitationTokenOptions>(_ => { });
        services.Replace(ServiceDescriptor.Scoped<IUserStore<OfficeUser>, ProtectedUserStore>());
        services.AddScoped<BootstrapManager>();
        services.AddScoped<AccountInvitations>();
        services.AddScoped<AccountSecurity>();
        services.AddScoped<AccessRevocation>();
        services.AddScoped<AuditTrail>();
        services.AddScoped<IBuildingAccessStore, BuildingAccessStore>();
        services.AddScoped<BuildingAccessActions>();
        services.AddScoped<IMaintenanceStore, MaintenanceStore>();
        services.AddScoped<MaintenanceActions>();
        services.AddScoped<IContractorStore, ContractorStore>();
        services.AddScoped<ContractorActions>();
        services.AddScoped<IKnowledgeStore, KnowledgeStore>();
        services.AddScoped<KnowledgeActions>();
        services.AddScoped<INotificationQueue>(sp => new NotificationQueue(sp.GetRequiredService<OfficeDbContext>(),
            sp.GetRequiredService<IDataProtectionProvider>(), sp.GetRequiredService<TimeProvider>(), new Uri("http://localhost:5081/")));
        services.AddScoped<NotificationDispatcher>();
        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().Database.MigrateAsync();

        seeder.Plan();
        await seeder.ReplayAsync(provider);
        await seeder.WriteAccountsAsync(provider, directory);
        seeder._clock.Now = now;
        seeder._actor.Current = null;
        await using var verification = provider.CreateAsyncScope();
        var report = await SeedVerification.CheckAsync(verification.ServiceProvider, seeder._actor, now, directory);
        // Let the connection pool release the file so the directory can be moved or deleted straight away.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return report;
    }

    private void At(When when, Func<IServiceProvider, Task> run, Mail mail = Mail.Send) =>
        _timeline.Add((when.Resolve(_now, _zone), _timeline.Count, mail, run));

    private void Plan()
    {
        var opened = DemoContent.Opened;
        At(opened, BootstrapAsync);
        At(opened with { Minute = opened.Minute + 10 }, sp => EnrollAsync(sp, DemoContent.Jordan));
        At(opened with { Minute = opened.Minute + 20 }, AddSecondManagerAsync);
        At(opened with { Hour = opened.Hour + 1, Minute = 5 }, sp => EnrollAsync(sp, DemoContent.Casey));
        for (var index = 0; index < DemoContent.Suites.Length; index++)
        {
            var name = DemoContent.Suites[index];
            At(opened with { Hour = opened.Hour + 1, Minute = 10 + index }, sp => AddSuiteAsync(sp, name));
        }
        foreach (var contractor in DemoContent.Contractors)
        {
            At(contractor.Added, sp => AddContractorAsync(sp, contractor));
            if (contractor.Deactivated is { } deactivated)
                At(deactivated, sp => DeactivateContractorAsync(sp, contractor));
        }
        foreach (var article in DemoContent.Articles)
        {
            var id = Guid.NewGuid();
            At(article.Drafted, sp => DraftArticleAsync(sp, id, article));
            if (article.Published is { } published)
                At(published, sp => PublishArticleAsync(sp, id, article));
        }
        foreach (var tenant in DemoContent.Tenants)
        {
            At(tenant.Invited, sp => InviteAsync(sp, tenant.Contact, tenant.Suite));
            At(tenant.Accepted, sp => AcceptAsync(sp, tenant.Contact));
        }
        At(DemoContent.FormerTenantRevoked, RevokeFormerTenantAsync);
        foreach (var invite in new[] { DemoContent.ExpiredInvitation, DemoContent.PendingInvitation })
            At(invite.Invited, sp => InviteAsync(sp, invite.Contact, invite.Suite));
        foreach (var request in DemoContent.Requests())
            PlanRequest(request);
    }

    private void PlanRequest(RequestScript request)
    {
        var previous = request.Submitted.Resolve(_now, _zone);
        foreach (var step in request.Steps)
        {
            var at = step.At.Resolve(_now, _zone);
            if (at <= previous)
                throw new InvalidOperationException($"Steps for \"{request.Title}\" are out of order.");
            previous = at;
        }
        var id = Guid.Empty;
        At(request.Submitted, async sp =>
        {
            _actor.Current = Tenant(request.Tenant);
            var result = await sp.GetRequiredService<MaintenanceActions>().SubmitAsync(new SubmitRequest(
                Guid.NewGuid(), _memberships[request.Tenant.Key], request.Title, request.Description, request.Location));
            Require(result.Code == MaintenanceCode.Success, $"submit \"{request.Title}\"");
            id = result.WorkOrderId!.Value;
        });
        foreach (var step in request.Steps)
            At(step.At, async sp =>
            {
                _actor.Current = step.By is null ? Tenant(request.Tenant) : Manager(step.By);
                var version = await sp.GetRequiredService<OfficeDbContext>().WorkOrders.AsNoTracking()
                    .Where(x => x.Id == id).Select(x => x.Version).SingleAsync();
                var result = await sp.GetRequiredService<MaintenanceActions>().ChangeAsync(new ChangeRequest(
                    Guid.NewGuid(), id, version, step.Action, step.Priority, step.Trade, step.Note, step.Internal,
                    ContractorId: step.Contractor is null ? null : _contractors[step.Contractor]), management: step.By is not null);
                Require(result.Code == MaintenanceCode.Success, $"{step.Action} on \"{request.Title}\"");
            }, step.Mail);
    }

    private async Task ReplayAsync(IServiceProvider provider)
    {
        var held = false;
        foreach (var moment in _timeline.OrderBy(x => x.At).ThenBy(x => x.Order))
        {
            if (moment.At >= _now)
                throw new InvalidOperationException("The demo timeline reaches into the future.");
            _clock.Now = moment.At;
            await using (var scope = provider.CreateAsyncScope())
                await moment.Run(scope.ServiceProvider);
            // Held mail must be the last mail of the run: delivering anything later would deliver it too.
            held |= moment.Mail == Mail.Hold;
            if (held)
                continue;
            _clock.Now = moment.At.AddSeconds(20);
            _transport.Fail = moment.Mail == Mail.Fail;
            await using var delivery = provider.CreateAsyncScope();
            while (await delivery.ServiceProvider.GetRequiredService<NotificationDispatcher>().ProcessOneAsync())
            { }
            _transport.Fail = false;
        }
    }

    private async Task BootstrapAsync(IServiceProvider sp)
    {
        await sp.GetRequiredService<BootstrapManager>().CreateAsync(DemoContent.Jordan.Email, DemoContent.Password);
        var users = sp.GetRequiredService<UserManager<OfficeUser>>();
        var manager = (await users.FindByEmailAsync(DemoContent.Jordan.Email))!;
        // The app has no page for display names yet; assignments show this name.
        manager.DisplayName = DemoContent.Jordan.Name;
        Require((await users.UpdateAsync(manager)).Succeeded, "name the first manager");
        _users[DemoContent.Jordan.Key] = manager.Id;
        _building = await sp.GetRequiredService<OfficeDbContext>().Buildings.Select(x => x.Id).SingleAsync();
    }

    // The app only creates the first manager, so the second is added directly, as an operator would.
    private async Task AddSecondManagerAsync(IServiceProvider sp)
    {
        var casey = DemoContent.Casey;
        var user = new OfficeUser { Email = casey.Email, UserName = casey.Email, EmailConfirmed = true, DisplayName = casey.Name };
        Require((await sp.GetRequiredService<UserManager<OfficeUser>>().CreateAsync(user, DemoContent.Password)).Succeeded, "create the second manager");
        var db = sp.GetRequiredService<OfficeDbContext>();
        db.BuildingManagers.Add(new BuildingManager { BuildingId = _building, UserId = user.Id });
        await db.SaveChangesAsync();
        _users[casey.Key] = user.Id;
    }

    // The same enrollment the Account pages run after a manager proves an authenticator code.
    private async Task EnrollAsync(IServiceProvider sp, Person manager)
    {
        var users = sp.GetRequiredService<UserManager<OfficeUser>>();
        var user = (await users.FindByIdAsync(_users[manager.Key]))!;
        Require((await users.ResetAuthenticatorKeyAsync(user)).Succeeded, "create an authenticator key");
        var security = sp.GetRequiredService<AccountSecurity>();
        await using var transaction = await security.LockForEnrollmentAsync(user, CancellationToken.None);
        Require((await security.CompleteEnrollmentAsync(user, firstSetup: true)).Succeeded, "enroll " + manager.Name);
        await transaction.CommitAsync();
    }

    private async Task AddSuiteAsync(IServiceProvider sp, string name)
    {
        _actor.Current = Manager(DemoContent.Jordan);
        var result = await sp.GetRequiredService<BuildingAccessActions>().AddSuiteAsync(_building, name, CancellationToken.None);
        Require(result.Code == SuiteCode.Success, "add suite " + name);
        _suites[name] = await sp.GetRequiredService<OfficeDbContext>().Suites.Where(x => x.Name == name).Select(x => x.Id).SingleAsync();
    }

    private async Task AddContractorAsync(IServiceProvider sp, ContractorSeed contractor)
    {
        _actor.Current = Manager(DemoContent.Jordan);
        var id = Guid.NewGuid();
        var code = await sp.GetRequiredService<ContractorActions>().SaveAsync(
            new SaveContractor(id, _building, null, contractor.Name, contractor.Trade, contractor.Email, contractor.Phone));
        Require(code == MaintenanceCode.Success, "add " + contractor.Name);
        _contractors[contractor.Name] = id;
    }

    private async Task DeactivateContractorAsync(IServiceProvider sp, ContractorSeed contractor)
    {
        _actor.Current = Manager(DemoContent.Casey);
        var actions = sp.GetRequiredService<ContractorActions>();
        var current = (await actions.ListAsync()).Single(x => x.Id == _contractors[contractor.Name]);
        var code = await actions.SaveAsync(new SaveContractor(
            current.Id, current.BuildingId, current.Version, current.Name, current.Trade, current.Email, current.Phone, Active: false));
        Require(code == MaintenanceCode.Success, "deactivate " + contractor.Name);
    }

    private async Task DraftArticleAsync(IServiceProvider sp, Guid id, ArticleSeed article)
    {
        _actor.Current = Manager(article.Editor);
        var review = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_now, _zone).Date.AddDays(180));
        var code = await sp.GetRequiredService<KnowledgeActions>().SaveAsync(
            new DraftArticle(id, _building, null, article.Title, article.Body, article.Audience, review));
        Require(code == ContentCode.Success, "draft \"" + article.Title + "\"");
    }

    private async Task PublishArticleAsync(IServiceProvider sp, Guid id, ArticleSeed article)
    {
        _actor.Current = Manager(article.Editor);
        var actions = sp.GetRequiredService<KnowledgeActions>();
        var draft = (await actions.ManageAsync(id)).Single();
        Require(await actions.PublishAsync(new PublishArticle(id, draft.Version, draft.Draft.Id)) == ContentCode.Success,
            "publish \"" + article.Title + "\"");
    }

    private async Task InviteAsync(IServiceProvider sp, Person contact, string suite)
    {
        _actor.Current = Manager(DemoContent.Jordan);
        var invitation = await sp.GetRequiredService<AccountInvitations>().IssueAsync(_suites[suite], contact.Email);
        Require(invitation is not null, "invite " + contact.Email);
        _invitations[contact.Key] = invitation!.Value;
        _users[contact.Key] = await sp.GetRequiredService<OfficeDbContext>().Invitations
            .Where(x => x.Id == invitation.Value).Select(x => x.UserId).SingleAsync();
    }

    // Accepts with the token from the queued invitation email, as following its link would.
    private async Task AcceptAsync(IServiceProvider sp, Person contact)
    {
        _actor.Current = null;
        var db = sp.GetRequiredService<OfficeDbContext>();
        var userId = _users[contact.Key];
        var payload = await db.AccountMessages.AsNoTracking().Where(x => x.UserId == userId && x.Kind == "Invitation")
            .Select(x => x.ProtectedPayload).SingleAsync();
        using var message = JsonDocument.Parse(sp.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("AllenOffice.AccountMessage.v1").Unprotect(payload));
        var accepted = await sp.GetRequiredService<AccountInvitations>().AcceptAsync(
            _invitations[contact.Key], message.RootElement.GetProperty("token").GetString()!, DemoContent.Password);
        Require(accepted, "accept the invitation for " + contact.Email);
        _memberships[contact.Key] = await db.Memberships.Where(x => x.UserId == userId).Select(x => x.Id).SingleAsync();
    }

    private async Task RevokeFormerTenantAsync(IServiceProvider sp)
    {
        _actor.Current = Manager(DemoContent.Jordan);
        var id = _memberships[DemoContent.FormerTenant.Contact.Key];
        var version = await sp.GetRequiredService<OfficeDbContext>().Memberships.AsNoTracking()
            .Where(x => x.Id == id).Select(x => x.Version).SingleAsync();
        var code = await sp.GetRequiredService<AccessRevocation>().RevokeAsync(id, version, DemoContent.FormerTenantReason);
        Require(code == AccessCode.Success, "revoke the former tenant");
    }

    private async Task WriteAccountsAsync(IServiceProvider provider, string directory)
    {
        await using var scope = provider.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
        var managers = new List<object>();
        foreach (var manager in new[] { DemoContent.Jordan, DemoContent.Casey })
            managers.Add(new
            {
                name = manager.Name,
                email = manager.Email,
                authenticatorKey = await users.GetAuthenticatorKeyAsync((await users.FindByIdAsync(_users[manager.Key]))!)
            });
        var former = DemoContent.FormerTenant;
        var accounts = new
        {
            password = DemoContent.Password,
            managers,
            tenants = DemoContent.Tenants.Where(x => x != former)
                .Select(x => new { suite = x.Suite, business = x.Business, name = x.Contact.Name, email = x.Contact.Email }),
            formerTenant = new { suite = former.Suite, business = former.Business, name = former.Contact.Name, email = former.Contact.Email, access = "revoked" },
            invitedNotAccepted = new[] { (DemoContent.PendingInvitation, "pending"), (DemoContent.ExpiredInvitation, "expired") }
                .Select(x => new { suite = x.Item1.Suite, business = x.Item1.Business, name = x.Item1.Contact.Name, email = x.Item1.Contact.Email, invitation = x.Item2 })
        };
        var path = Path.Combine(directory, "accounts.json");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var file = new FileStream(path, options);
        await JsonSerializer.SerializeAsync(file, accounts, new JsonSerializerOptions { WriteIndented = true });
    }

    private Actor Manager(Person manager) => new(_users[manager.Key], new HashSet<Guid> { _building }, true, true);
    private Actor Tenant(Person tenant) => new(_users[tenant.Key], new HashSet<Guid>(), false, false);

    private static void Require(bool succeeded, string what)
    {
        if (!succeeded)
            throw new InvalidOperationException("The demo seed could not " + what + ".");
    }
}

internal sealed class SeedClock : TimeProvider
{
    public DateTimeOffset Now { get; set; }
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class SeedActor : ICurrentActor
{
    public Actor? Current { get; set; }
    public Task<Actor?> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
}

// Sends nothing. It only decides whether a queued message ends up recorded as delivered or failed.
internal sealed class SeedTransport : IEmailTransport
{
    public bool Fail { get; set; }
    public Task<DeliveryResult> SendAsync(EmailEnvelope envelope, CancellationToken cancellationToken) =>
        Task.FromResult(Fail ? new DeliveryResult(DeliveryOutcome.PermanentFailure, "SeededFailure") : new DeliveryResult(DeliveryOutcome.Delivered));
}
