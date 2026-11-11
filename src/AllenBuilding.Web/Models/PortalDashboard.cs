using AllenBuilding.Application.Access;
using AllenBuilding.Application.Maintenance;

namespace AllenBuilding.Web.Models;

public sealed record ManagerDashboard(ManagerSummary Summary, int DeliveryProblems, bool MoreDeliveryProblems,
    IReadOnlyList<RequestSummary> NewestRequests, DateTimeOffset ObservedAt);

public sealed record PortalDashboard(bool Tenant, IReadOnlyList<RequestSummary> OpenRequests, ManagerDashboard? Managing,
    bool SecuritySetupAvailable, bool BillingEnabled);
