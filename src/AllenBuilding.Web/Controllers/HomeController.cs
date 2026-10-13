using System.Diagnostics;
using AllenBuilding.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace AllenBuilding.Web.Controllers;

public class HomeController : Controller
{
    public IActionResult Index()
    {
        return View();
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    [IgnoreAntiforgeryToken] // Read-only error rendering must also work after a failed POST.
    public IActionResult Error()
    {
        Response.StatusCode = StatusCodes.Status500InternalServerError;
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    [IgnoreAntiforgeryToken] // Never execute or replay an action when rendering a status response.
    public IActionResult Status([FromQuery] int code)
    {
        code = code is >= 400 and <= 599 ? code : 404;
        Response.StatusCode = code;
        return View("Status", code);
    }
}
