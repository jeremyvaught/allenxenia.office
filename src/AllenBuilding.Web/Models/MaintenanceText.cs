using AllenBuilding.Domain.Maintenance;

namespace AllenBuilding.Web.Models;

public static class MaintenanceText
{
    public static string Status(WorkOrderStatus status) => status == WorkOrderStatus.InProgress ? "In progress" : status.ToString();
    public static string Trade(Trade? trade) => trade == Domain.Maintenance.Trade.HeatingCooling ? "Heating and cooling" : trade?.ToString() ?? "";
    public static string History(string action) => action switch
    {
        "Submitted" => "Request submitted",
        "Triage" => "Priority and trade set",
        "Assign" => "Assignment updated",
        "AssignSelf" => "Assigned to manager",
        "Start" => "Work started",
        "Complete" => "Work completed",
        "Close" => "Work verified",
        "Cancel" => "Request cancelled",
        "Reopen" => "Request reopened",
        "Comment" => "Comment added",
        _ => "Request updated"
    };
}
