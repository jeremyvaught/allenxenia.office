using AllenBuilding.Web.Hosting;

namespace AllenBuilding.IntegrationTests;

public sealed class AssistantBudgetTests
{
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
