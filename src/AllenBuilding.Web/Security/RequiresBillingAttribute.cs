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

// What the billing pages may offer. FinancialReview covers the exception queue and pending closures.
public sealed record BillingFeatures(bool Planning, bool FinancialReview);

// The financial-exception pages answer 404 unless payment collection or one of its review stages is switched on.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequiresFinancialReviewAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        if (!context.HttpContext.RequestServices.GetRequiredService<BillingFeatures>().FinancialReview)
            context.Result = new NotFoundResult();
    }

    public void OnResourceExecuted(ResourceExecutedContext context) { }
}
