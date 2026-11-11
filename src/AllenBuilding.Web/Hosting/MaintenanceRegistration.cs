using AllenBuilding.Application.Maintenance;
using AllenBuilding.Infrastructure.Persistence;

namespace AllenBuilding.Web.Hosting;

public static class MaintenanceRegistration
{
    public static void AddOfficeMaintenance(this WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<IMaintenanceStore, MaintenanceStore>();
        builder.Services.AddScoped<MaintenanceActions>();
        builder.Services.AddScoped<IContractorStore, ContractorStore>();
        builder.Services.AddScoped<ContractorActions>();
    }
}
