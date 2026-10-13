using System.Data.Common;
using AllenBuilding.Application.Reporting;
using AllenBuilding.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AllenBuilding.Web.Controllers;

[Authorize(Policy = OfficeSecurity.ManagerPolicy), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ReportsController(ManagementReportQueries queries, ILogger<ReportsController> logger) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Rent(RentBalanceQuery query, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || !query.IsValid) return BadRequest();
        try { var report = await queries.RentBalancesAsync(query, cancellationToken); return report is null ? NotFound() : View(report); }
        catch (Exception exception) when (exception is TimeoutException or DbException)
        {
            logger.LogWarning("Rent balance report unavailable ({FailureType})", exception.GetType().Name);
            ViewData["RetryAction"] = "Rent"; Response.StatusCode = 503; return View("Unavailable");
        }
    }

    [HttpGet]
    public async Task<IActionResult> Maintenance(MaintenanceAgingQuery query, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || !query.IsValid) return BadRequest();
        try
        {
            var report = await queries.MaintenanceAgingAsync(query, cancellationToken);
            return report is null ? NotFound() : View(report);
        }
        catch (Exception exception) when (exception is TimeoutException or DbException)
        {
            logger.LogWarning("Maintenance aging report unavailable ({FailureType})", exception.GetType().Name);
            Response.StatusCode = 503; return View("Unavailable");
        }
    }
}
