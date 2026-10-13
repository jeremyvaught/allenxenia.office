using Microsoft.AspNetCore.Mvc;

namespace AllenBuilding.Web.Controllers;

public sealed class PreferencesController : Controller
{
    public const string ThemeCookie = "allen-office-theme";
    [HttpPost("/preferences/theme"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Theme(string? value, string? returnUrl)
    {
        if (value is not ("system" or "light" or "dark")) return BadRequest();
        Response.Cookies.Append(ThemeCookie, value, new CookieOptions
        {
            Path = "/",
            MaxAge = TimeSpan.FromDays(365),
            SameSite = SameSiteMode.Lax,
            Secure = Request.IsHttps,
            HttpOnly = false,
            IsEssential = true
        });
        return LocalRedirect(returnUrl?.Length <= 2048 && Url.IsLocalUrl(returnUrl) ? returnUrl : "/");
    }
}
