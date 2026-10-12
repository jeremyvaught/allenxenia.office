using AllenBuilding.Domain.Maintenance;

namespace AllenBuilding.DomainTests;

public sealed class MaintenanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static WorkOrder NewRequest() => WorkOrder.Submit(Guid.NewGuid(), Guid.NewGuid(), "tenant", "Leaking sink", "Water under basin", "Suite kitchen", Now);

    [Fact]
    public void Completion_requires_separate_manager_verification()
    {
        var request = NewRequest();
        request.Triage(Priority.High, Trade.Plumbing, Now);
        request.Assign("manager", null, Now);
        request.Start(Now);
        request.Complete("Replaced the supply fitting", Now);
        Assert.Equal(WorkOrderStatus.Completed, request.Status);
        var completedVersion = request.Version;
        request.Close(Now);
        Assert.Equal(WorkOrderStatus.Closed, request.Status);
        Assert.NotEqual(completedVersion, request.Version);
    }

    [Fact]
    public void Reassignment_preserves_started_work()
    {
        var request = NewRequest();
        request.Triage(Priority.Normal, Trade.General, Now);
        request.Assign("manager", null, Now);
        request.Start(Now);
        var contractor = Guid.NewGuid();
        request.Assign(null, contractor, Now);
        Assert.Equal(WorkOrderStatus.InProgress, request.Status);
        Assert.Null(request.AssignedManagerId);
        Assert.Equal(contractor, request.AssignedContractorId);
    }

    [Fact]
    public void Reopen_requires_new_triage_and_assignment()
    {
        var request = NewRequest();
        request.Triage(Priority.High, Trade.Plumbing, Now);
        request.Assign("manager", null, Now);
        request.Start(Now);
        request.Complete("Repaired", Now);
        request.Close(Now);
        request.Reopen("Still leaking", Now.AddDays(1));
        Assert.Equal(WorkOrderStatus.Submitted, request.Status);
        Assert.Null(request.Priority);
        Assert.Null(request.Trade);
        Assert.Null(request.AssignedManagerId);
        Assert.Null(request.Resolution);
        Assert.Throws<InvalidOperationException>(() => request.Start(Now));
    }

    [Fact]
    public void Invalid_assignment_cannot_partially_change_a_request()
    {
        var request = NewRequest();
        request.Triage(Priority.Normal, Trade.General, Now);
        var originalVersion = request.Version;
        Assert.Throws<ArgumentException>(() => request.Assign("manager", Guid.NewGuid(), Now));
        Assert.Equal(originalVersion, request.Version);
        Assert.Null(request.AssignedManagerId);
        Assert.Null(request.AssignedContractorId);
    }

    [Fact]
    public void Unsupported_transition_or_enum_cannot_change_version()
    {
        var request = NewRequest(); var originalVersion = request.Version;
        Assert.Throws<InvalidOperationException>(() => request.Close(Now));
        Assert.Throws<ArgumentException>(() => request.Triage((Priority)99, Trade.General, Now));
        Assert.Equal(WorkOrderStatus.Submitted, request.Status);
        Assert.Equal(originalVersion, request.Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_titles_fail_before_a_request_is_created(string title)
    {
        Assert.Throws<ArgumentException>(() => WorkOrder.Submit(Guid.NewGuid(), Guid.NewGuid(), "tenant", title, "Description", "Kitchen", Now));
    }
}
