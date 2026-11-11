using AllenBuilding.Application.Reporting;
using AllenBuilding.Infrastructure.Persistence;

namespace AllenBuilding.Web.Hosting;

public static class ReportingRegistration
{
    public static void AddOfficeReporting(this WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<IManagementReportReader, ManagementReportReader>();
        builder.Services.AddScoped<ManagementReportQueries>();
        builder.Services.AddScoped<ICompletionReportReader, ManagementReportReader>();
        builder.Services.AddScoped<CompletionReportQueries>();
    }
}
