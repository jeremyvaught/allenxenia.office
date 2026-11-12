using AllenBuilding.Application.Access;
using AllenBuilding.Application.Maintenance;

namespace AllenBuilding.Web.Models;

public sealed record ManagerDashboard(ManagerSummary Summary, int DeliveryProblems, bool MoreDeliveryProblems,
    IReadOnlyList<RequestSummary> NewestRequests, DateTimeOffset ObservedAt);

// Money owed and the overdue part of it: across the building for a manager, on their own accounts for a tenant.
public sealed record BillingGlance(long OwedMinorUnits, long OverdueMinorUnits);

public sealed record PortalDashboard(bool Tenant, IReadOnlyList<RequestSummary> OpenRequests, ManagerDashboard? Managing,
    bool SecuritySetupAvailable, bool BillingEnabled, BillingGlance? BuildingBilling = null, BillingGlance? TenantBilling = null);
