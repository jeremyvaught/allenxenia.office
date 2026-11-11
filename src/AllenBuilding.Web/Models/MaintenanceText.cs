using AllenBuilding.Domain.Maintenance;

namespace AllenBuilding.Web.Models;

public static class MaintenanceText
{
    public static string Status(WorkOrderStatus status) => status == WorkOrderStatus.InProgress ? "In progress" : status.ToString();
    public static string Trade(Trade? trade) => trade == Domain.Maintenance.Trade.HeatingCooling ? "Heating and cooling" : trade?.ToString() ?? "";
    // Comments name their author, so a manager's reply and a tenant's comment cannot be confused.
    public static string HistoryTitle(Application.Maintenance.HistoryEntry entry)
    {
        if (entry.Action != "Comment")
            return History(entry.Action);
        if (entry.Mine)
            return entry.Internal ? "Your internal note" : "Your comment";
        return (entry.Internal ? "Internal note from " : "Comment from ") + (entry.By ?? "building management");
    }

    public static string History(string action) => action switch
    {
        "Submitted" => "Request submitted",
        "SubmittedOnBehalf" => "Request recorded by manager for tenant",
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
