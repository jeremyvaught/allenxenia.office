using System.Threading.RateLimiting;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using AllenBuilding.Web.Security;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var migrate = args.Contains("--migrate", StringComparer.Ordinal);
var bootstrap = args.Contains("--bootstrap-manager", StringComparer.Ordinal);
var dispatch = args.Contains("--dispatch-mail-once", StringComparer.Ordinal);
if (new[] { migrate, bootstrap, dispatch }.Count(x => x) > 1) throw new InvalidOperationException("Choose one operator command at a time.");
var builder = WebApplication.CreateBuilder(args.Where(x => x != "--migrate" && x != "--bootstrap-manager" && x != "--dispatch-mail-once").ToArray());
if (builder.Environment.IsEnvironment("Testing")) builder.WebHost.UseStaticWebAssets();
builder.Services.AddControllersWithViews(options =>
    options.Filters.Add(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOfficeStorage(builder.Configuration, builder.Environment);
builder.AddOfficeSecurity();
builder.AddOfficeNotifications();
builder.Services.AddScoped<AllenBuilding.Application.Maintenance.IMaintenanceStore, MaintenanceStore>();
builder.Services.AddScoped<AllenBuilding.Application.Maintenance.MaintenanceActions>();
builder.Services.AddScoped<AccessRevocation>();
builder.Services.AddScoped<AccountAccess>();
builder.Services.AddScoped<AllenBuilding.Application.Maintenance.IContractorStore, ContractorStore>();
builder.Services.AddScoped<AllenBuilding.Application.Maintenance.ContractorActions>();
builder.Services.AddScoped<AllenBuilding.Application.Knowledge.IKnowledgeStore, KnowledgeStore>();
builder.Services.AddScoped<AllenBuilding.Application.Knowledge.KnowledgeActions>();
builder.Services.AddScoped<AllenBuilding.Application.Billing.IBillingStore, BillingStore>();
builder.Services.AddScoped<AllenBuilding.Application.Billing.BillingActions>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("Accounts", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();

if (migrate)
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().Database.MigrateAsync();
    return;
}

if (bootstrap)
{
    if (Console.IsInputRedirected) throw new InvalidOperationException("Bootstrap requires an interactive terminal; never pass a password as an argument.");
    Console.Write("Manager email: ");
    var email = Console.ReadLine() ?? "";
    Console.Write("Password (hidden, at least 12 characters): ");
    var password = new System.Text.StringBuilder();
    ConsoleKeyInfo key;
    while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
    {
        if (key.Key == ConsoleKey.Backspace && password.Length > 0) password.Length--;
        else if (!char.IsControl(key.KeyChar)) password.Append(key.KeyChar);
    }
    Console.WriteLine();
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<BootstrapManager>().CreateAsync(email, password.ToString());
    Console.WriteLine("Manager created. Sign in and enroll an authenticator before managing the building.");
    return;
}

if (dispatch)
{
    if (!app.Environment.IsDevelopment() || builder.Configuration["Notifications:Mode"] != "LocalInbox")
        throw new InvalidOperationException("This operator command only dispatches to the local development inbox.");
    using var scope = app.Services.CreateScope();
    var dispatcher = scope.ServiceProvider.GetRequiredService<AllenBuilding.Application.Notifications.NotificationDispatcher>();
    var count = 0;
    while (count < 20 && await dispatcher.ProcessOneAsync()) count++;
    Console.WriteLine($"Processed {count} local notifications. Local inbox: src/AllenBuilding.Web/.data/mail (never committed).");
    return;
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseRouting();
app.Use(async (context, next) =>
{
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    await next(context);
});
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapGet("/health/live", () => Results.Ok(new { status = "Healthy" }));
app.MapStaticAssets();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}").WithStaticAssets();
app.Run();

public partial class Program;
