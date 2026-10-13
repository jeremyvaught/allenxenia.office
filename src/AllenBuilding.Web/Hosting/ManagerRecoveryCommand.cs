using AllenBuilding.Application.Access;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;

namespace AllenBuilding.Web.Hosting;

public static class ManagerRecoveryCommand
{
    public static async Task RunAsync(WebApplication app, IConfiguration configuration)
    {
        if (Console.IsInputRedirected) throw new InvalidOperationException("Manager recovery requires an interactive authorized deployment terminal.");
        var policy = new OperatorRecoveryPolicy(configuration.GetValue<bool>("Operations:ManagerRecoveryEnabled"), configuration["Operations:RecoveryOperatingSystemUser"] ?? "");
        if (!policy.AllowsCurrentProcess) throw new InvalidOperationException("Manager recovery is disabled or this operating-system identity is not the configured deployment operator.");
        Console.WriteLine("Use the pre-established owner verification procedure and authenticated deployment access. Email/password possession alone is insufficient.");
        Console.Write("Manager email: "); var email = Console.ReadLine()?.Trim();
        using var scope = app.Services.CreateScope(); var services = scope.ServiceProvider;
        var users = services.GetRequiredService<UserManager<OfficeUser>>(); var target = await users.FindByEmailAsync(email ?? "");
        if (target is null || !target.TwoFactorEnabled || string.IsNullOrEmpty(target.SecurityStamp)) throw new InvalidOperationException("No eligible reviewed manager recovery is available.");
        Console.Write("Completed owner-verification record reference (no codes/secrets): "); var reference = Console.ReadLine() ?? "";
        Console.Write("Recovery reason (no secrets): "); var reason = Console.ReadLine() ?? "";
        Console.WriteLine("This revokes existing sessions and replaces the factor/recovery codes. The password and grants remain; fresh enrollment and recovery codes are required.");
        Console.Write("Type RECOVER after independently verifying the owner: ");
        if (Console.ReadLine() != "RECOVER") { Console.WriteLine("No recovery was applied."); return; }
        var result = await new OperatorMfaRecovery(services.GetRequiredService<OfficeDbContext>(), users,
            services.GetRequiredService<AccountInvitations>(), policy)
            .ExecuteAsync(new(target.Id, target.SecurityStamp, reference, reason));
        if (result != AccessCode.Success) throw new InvalidOperationException("The reviewed recovery could not be applied. Recheck eligibility and current account state.");
        Console.WriteLine("Recovery committed with audit and queued security notice. Sign in with the existing password, enroll a fresh authenticator, then replenish recovery codes.");
    }
}
