using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using AllenBuilding.Application.Access;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AllenBuilding.Web.Security;

public static class OfficeSecurity
{
    public const string ManagerPolicy = "ManagerWithMfa";
    public const string RecentManagerPolicy = "ManagerWithRecentMfa";
    public const string PasswordTime = "office.password_time";
    public const string FactorTime = "office.factor_time";
    // Persistent encryption namespace, intentionally retained for existing MFA seeds,
    // cookies and tokens. A display-name change must never change this identifier.
    public const string ProtectionApplicationName = "AllenXeniaOffice.Build";

    public static void AddOfficeSecurity(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddIdentity<OfficeUser, IdentityRole>(options =>
        {
            options.Password.RequiredLength = 12;
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;
            options.User.RequireUniqueEmail = true;
            options.SignIn.RequireConfirmedEmail = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        }).AddEntityFrameworkStores<OfficeDbContext>().AddDefaultTokenProviders()
            .AddTokenProvider<InvitationTokenProvider>("Invitation");
        services.Configure<InvitationTokenOptions>(_ => { });
        services.AddScoped<BootstrapManager>();
        services.AddScoped<AccountInvitations>();
        services.AddScoped<OperatorEnrollment>();
        services.Replace(ServiceDescriptor.Scoped<IUserStore<OfficeUser>, ProtectedUserStore>());
        services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = TimeSpan.FromHours(2));
        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "AllenOffice.Session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing")
                ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.LoginPath = "/Account/Login";
            options.AccessDeniedPath = "/Account/Denied";
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = false;
            options.Events.OnValidatePrincipal = async context =>
            {
                var signIn = context.HttpContext.RequestServices.GetRequiredService<SignInManager<OfficeUser>>();
                var user = await signIn.ValidateSecurityStampAsync(context.Principal);
                // Password lockout prevents new sign-ins; revocation uses the security stamp.
                // Unauthenticated guesses must not terminate an already verified session.
                if (user is null || !user.EmailConfirmed ||
                    (context.Principal!.HasClaim("amr", "mfa") && !user.TwoFactorEnabled))
                {
                    context.RejectPrincipal();
                    await signIn.SignOutAsync();
                }
            };
        });
        services.Configure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(IdentityConstants.TwoFactorUserIdScheme,
            options => options.ExpireTimeSpan = TimeSpan.FromMinutes(5));
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentActor, CurrentActor>();
        services.AddScoped<IAuthorizationHandler, ManagerAuthorization>();
        services.AddAuthorization(options =>
        {
            options.AddPolicy(ManagerPolicy, policy => policy.RequireAuthenticatedUser().AddRequirements(new ManagerRequirement(false)));
            options.AddPolicy(RecentManagerPolicy, policy => policy.RequireAuthenticatedUser().AddRequirements(new ManagerRequirement(true)));
        });

        var keyPath = builder.Configuration["Protection:KeyDirectory"];
        var production = !builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing");
        if (production && string.IsNullOrWhiteSpace(keyPath))
            throw new InvalidOperationException("Production requires a persistent Protection:KeyDirectory and wrapping certificate.");
        keyPath ??= Path.Combine(builder.Environment.ContentRootPath, ".data", "keys");
        if (production) ProtectionFiles.ValidateDirectory(keyPath, builder.Environment.ContentRootPath);
        Directory.CreateDirectory(keyPath);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var protection = services.AddDataProtection().SetApplicationName(ProtectionApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(keyPath));
        var certificate = builder.Configuration["Protection:CertificatePath"];
        if (production && string.IsNullOrWhiteSpace(certificate))
            throw new InvalidOperationException("Production requires Protection:CertificatePath to wrap protected keys.");
        if (!string.IsNullOrWhiteSpace(certificate))
        {
            try
            {
                if (production) ProtectionFiles.ValidateCertificate(certificate, builder.Environment.ContentRootPath);
                var wrapping = X509CertificateLoader.LoadPkcs12FromFile(certificate,
                    builder.Configuration["Protection:CertificatePassword"], OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
                if (!wrapping.HasPrivateKey) throw new InvalidOperationException();
                protection.ProtectKeysWithCertificate(wrapping);
            }
            catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            { throw new InvalidOperationException("The wrapping certificate must be a readable private certificate with its private key; keep it outside releases with owner-only file/parent permissions."); }
        }
    }

    public static Claim TimeClaim(string type, DateTimeOffset time) => new(type, time.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
}

public sealed class CurrentActor(IHttpContextAccessor http, SignInManager<OfficeUser> signIn,
    OfficeDbContext db, TimeProvider time) : ICurrentActor
{
    public async Task<Actor?> GetAsync(CancellationToken cancellationToken = default)
    {
        var principal = http.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true) return null;
        var userId = signIn.UserManager.GetUserId(principal);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, cancellationToken);
        var stamp = principal.FindFirstValue(signIn.UserManager.Options.ClaimsIdentity.SecurityStampClaimType);
        if (user is null || stamp != user.SecurityStamp || !user.EmailConfirmed) return null;
        var buildings = (await db.BuildingManagers.AsNoTracking().Where(x => x.UserId == user.Id)
            .Select(x => x.BuildingId).ToListAsync(cancellationToken)).ToHashSet();
        var mfa = principal.HasClaim("amr", "mfa") && user.TwoFactorEnabled && !user.MustReplaceRecoveryCodes;
        bool Recent(string claim) => long.TryParse(principal.FindFirstValue(claim), out var seconds) &&
            seconds <= time.GetUtcNow().ToUnixTimeSeconds() && time.GetUtcNow().ToUnixTimeSeconds() - seconds <= 300;
        return new Actor(user.Id, buildings, mfa, mfa && Recent(OfficeSecurity.PasswordTime) && Recent(OfficeSecurity.FactorTime));
    }
}

public sealed record ManagerRequirement(bool Recent) : IAuthorizationRequirement;
public sealed class ManagerAuthorization(ICurrentActor current) : AuthorizationHandler<ManagerRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ManagerRequirement requirement)
    {
        var actor = await current.GetAsync();
        if (actor is { ChallengedMfa: true } && actor.ManagedBuildings.Count > 0 && (!requirement.Recent || actor.RecentMfa))
            context.Succeed(requirement);
    }
}
