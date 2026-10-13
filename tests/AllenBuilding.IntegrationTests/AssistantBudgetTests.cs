using System.Net;
using System.Security.Claims;
using AllenBuilding.Web.Hosting;
using Microsoft.AspNetCore.Http;

namespace AllenBuilding.IntegrationTests;

public sealed class AssistantBudgetTests
{
    [Fact]
    public void An_authenticated_budget_survives_ip_changes_and_untrusted_identity_claims_cannot_select_it()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "confirmed-person")], "Identity.Application"))
        };
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        var partition = AssistantRequestIdentity.Partition(context); var budget = new AssistantBudget(new Clock());
        for (var attempt = 0; attempt < 20; attempt++) Assert.True(budget.TryAdmit(partition));
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.11");
        Assert.False(budget.TryAdmit(AssistantRequestIdentity.Partition(context)));
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "confirmed-person")]));
        var anonymous = AssistantRequestIdentity.Partition(context);
        Assert.NotEqual(partition, anonymous); Assert.StartsWith("ip:", anonymous);
    }
    [Fact]
    public void Hourly_limit_counts_failures_and_global_budget_survives_partition_rotation()
    {
        var clock = new Clock(); var budget = new AssistantBudget(clock);
        for (var i = 0; i < 20; i++) Assert.True(budget.TryAdmit("same-address"));
        Assert.False(budget.TryAdmit("same-address")); clock.Now = clock.Now.AddHours(1);
        for (var i = 0; i < 80; i++) Assert.True(budget.TryAdmit("address-" + i));
        Assert.False(budget.TryAdmit("new-address")); clock.Now = clock.Now.AddDays(1); Assert.True(budget.TryAdmit("new-address"));
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
