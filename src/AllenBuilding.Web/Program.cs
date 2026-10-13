using System.Threading.RateLimiting;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using AllenBuilding.Web.Diagnostics;
using AllenBuilding.Web.Hosting;
using AllenBuilding.Web.Security;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var migrate = args.Contains("--migrate", StringComparer.Ordinal);
var bootstrap = args.Contains("--bootstrap-manager", StringComparer.Ordinal);
var dispatch = args.Contains("--dispatch-mail-once", StringComparer.Ordinal);
var recover = args.Contains("--recover-manager-mfa", StringComparer.Ordinal);
if (new[] { migrate, bootstrap, dispatch, recover }.Count(x => x) > 1) throw new InvalidOperationException("Choose one operator command at a time.");
var builder = WebApplication.CreateBuilder(args.Where(x => x != "--migrate" && x != "--bootstrap-manager" && x != "--dispatch-mail-once" && x != "--recover-manager-mfa").ToArray());
builder.AddOfficeRuntimeConfiguration(migrate);
builder.AddOfficeProxy();
builder.AddOfficeObservability();
if (builder.Environment.IsEnvironment("Testing")) builder.WebHost.UseStaticWebAssets();
builder.Services.AddControllersWithViews(options =>
    options.Filters.Add(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOfficeStorage(builder.Configuration, builder.Environment, migrations: migrate);
builder.Services.AddHealthChecks().AddCheck<DatabaseReadiness>("office-database");
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
// Admission is wired but remains unavailable until provider dispatch and reconciliation are verified.
var isolatedPayments = builder.Environment.IsEnvironment("Testing") && builder.Configuration.GetValue<bool>("Billing:CollectionReviewEnabled");
builder.Services.AddSingleton(isolatedPayments ? new AllenBuilding.Application.Billing.CollectionPolicy(true,
    builder.Configuration["Stripe:ProviderAccount"] ?? "", false,
    new AllenBuilding.Domain.Billing.CheckoutProfile(builder.Configuration["Stripe:PublicOrigin"] ?? "", builder.Configuration["Stripe:PaymentMethodConfiguration"] ?? ""))
    : new AllenBuilding.Application.Billing.CollectionPolicy(false, "", false));
builder.Services.AddScoped<AllenBuilding.Application.Billing.ICollectionStore, CollectionStore>();
builder.Services.AddScoped<AllenBuilding.Application.Billing.CollectionActions>();
builder.Services.AddScoped<AllenBuilding.Application.Billing.IPayerCollectionReader, PayerCollectionReader>();
builder.Services.AddScoped<AllenBuilding.Application.Billing.PayerCollections>();
builder.Services.AddScoped<PaymentReviewTickets>();
// Protected consent pages may be exercised by isolated fixtures; no provider dispatcher is registered yet.
var isolatedAutopay = builder.Environment.IsEnvironment("Testing") && builder.Configuration.GetValue<bool>("Billing:AutopayReviewEnabled");
builder.Services.AddSingleton(new AllenBuilding.Application.Billing.AutopayPolicy(isolatedAutopay, builder.Configuration["Stripe:ProviderAccount"] ?? "", false,
    isolatedAutopay ? new AllenBuilding.Domain.Billing.CheckoutProfile(builder.Configuration["Stripe:PublicOrigin"] ?? "", builder.Configuration["Stripe:PaymentMethodConfiguration"] ?? "") : null));
builder.Services.AddScoped<AllenBuilding.Application.Billing.IAutopayStore, AutopayStore>();
builder.Services.AddScoped<AllenBuilding.Application.Billing.IAutopayMethodStore>(s => (AutopayStore)s.GetRequiredService<AllenBuilding.Application.Billing.IAutopayStore>());
builder.Services.AddScoped<AllenBuilding.Application.Billing.AutopayMethodHandoffs>();
builder.Services.AddScoped<AllenBuilding.Application.Billing.AutopayPlanning>();
builder.Services.AddScoped<AutopayReviewTickets>();
builder.Services.AddSingleton(new AllenBuilding.Application.Billing.FinancialRecoveryPolicy(false));
builder.Services.AddScoped<AllenBuilding.Application.Billing.IFinancialExceptionStore, FinancialExceptionStore>();
builder.Services.AddScoped<AllenBuilding.Application.Billing.FinancialExceptionActions>();
// Only isolated browser fixtures can opt in before whole-payment recovery is finished; ordinary runtime stays disabled.
builder.Services.AddSingleton(new AllenBuilding.Application.Billing.RefundDispositionPolicy(builder.Environment.IsEnvironment("Testing") && builder.Configuration.GetValue<bool>("Billing:RefundReviewEnabled")));
builder.Services.AddScoped<AllenBuilding.Application.Billing.IRefundDispositionStore, RefundDispositionStore>();
builder.Services.AddScoped<AllenBuilding.Application.Billing.RefundDispositionActions>();
builder.Services.AddSingleton(new AllenBuilding.Application.Billing.CashSourceRecoveryPolicy(builder.Environment.IsEnvironment("Testing") && builder.Configuration.GetValue<bool>("Billing:CashSourceReviewEnabled")));
builder.Services.AddScoped<AllenBuilding.Application.Billing.ICashSourceRecoveryStore, CashSourceRecoveryStore>();
builder.Services.AddScoped<AllenBuilding.Application.Billing.CashSourceRecoveryActions>();
// No hosted reader or Stripe credential is registered while full payment agreement is unfinished.
builder.Services.AddScoped<AllenBuilding.Application.Billing.IPaymentWebhookReceiver, AllenBuilding.Application.Billing.DisabledPaymentWebhookReceiver>();
builder.Services.AddScoped<BillingReviewTickets>();
builder.Services.AddScoped<AllenBuilding.Application.Reporting.IManagementReportReader, ManagementReportReader>();
builder.Services.AddScoped<AllenBuilding.Application.Reporting.ManagementReportQueries>();
builder.Services.AddScoped<AllenBuilding.Application.Reporting.ICompletionReportReader, ManagementReportReader>();
builder.Services.AddScoped<AllenBuilding.Application.Reporting.CompletionReportQueries>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddConcurrencyLimiter("Readiness", limiter => { limiter.PermitLimit = 2; limiter.QueueLimit = 0; });
    options.AddConcurrencyLimiter("PaymentActions", limiter => { limiter.PermitLimit = 2; limiter.QueueLimit = 0; });
    options.AddConcurrencyLimiter("PaymentWebhook", limiter => { limiter.PermitLimit = 2; limiter.QueueLimit = 0; });
    options.AddPolicy("Accounts", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();

if (migrate)
{
    try
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().Database.MigrateAsync();
    }
    catch (Exception)
    {
        Console.Error.WriteLine("Migration failed. Keep writers stopped and inspect the applied schema through the authorized operator; do not continue activation.");
        Environment.ExitCode = 1;
    }
    finally { await app.DisposeAsync(); }
    return;
}

if (recover)
{
    try { await ManagerRecoveryCommand.RunAsync(app, builder.Configuration); }
    catch (Exception)
    {
        Console.Error.WriteLine("Manager recovery failed or was not authorized. No successful recovery is claimed; check the audited account state through the authorized operator.");
        Environment.ExitCode = 1;
    }
    finally { await app.DisposeAsync(); }
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

app.UseOfficeProxy();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseRouting();
app.Use(OfficeObservability.RequestAsync);
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
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new { status = report.Status == Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy ? "Ready" : "Unavailable" }, context.RequestAborted);
    }
}).RequireRateLimiting("Readiness");
app.MapStaticAssets();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}").WithStaticAssets();
app.Run();

public partial class Program;
