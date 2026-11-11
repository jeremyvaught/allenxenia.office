using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AllenBuilding.Web.Security;

// Billing is dormant in production. Its pages answer 404 unless Billing:PlanningEnabled is set.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequiresBillingAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        if (!configuration.GetValue<bool>("Billing:PlanningEnabled"))
            context.Result = new NotFoundResult();
    }

    public void OnResourceExecuted(ResourceExecutedContext context) { }
}
