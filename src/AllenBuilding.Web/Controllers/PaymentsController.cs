using AllenBuilding.Application.Access;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Web.Models;
using AllenBuilding.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AllenBuilding.Web.Controllers;

public sealed record PaymentSelection(BillingAccount Account, IReadOnlyList<ReviewedCollectionBalance> Balances, IReadOnlyList<PayerPaymentSummary> Recent);
public sealed record PaymentReviewProblem(string Heading, string Message);
[Authorize, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PaymentsController(BillingActions billing, CollectionActions actions, PayerCollections payments, CollectionPolicy policy,
    ICurrentActor current, PaymentReviewTickets tickets, TimeProvider time) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(Guid accountId)
    {
        if (!policy.Enabled) return NotFound();
        var account = (await billing.ReadAsync(false, accountId: accountId)).SingleOrDefault();
        if (account is null) return NotFound();
        // Bounded choice page; the reviewed action itself is limited to 25 items.
        var balances = new List<ReviewedCollectionBalance>();
        foreach (var group in account.Obligations.Chunk(25)) balances.AddRange(await actions.ReviewAsync(account.Id, group.Select(x => x.Id).ToArray()));
        var recent = await payments.RecentAsync(accountId);
        if ((await billing.ReadAsync(false, accountId: accountId)).SingleOrDefault() is null) return NotFound();
        return View(new PaymentSelection(account, balances, recent));
    }
    [HttpPost, RequestSizeLimit(32768), EnableRateLimiting("PaymentActions")]
    public async Task<IActionResult> Prepare(Guid accountId, Guid operationId, Guid[]? obligationIds)
    {
        if (!policy.Enabled) return NotFound();
        if (!ModelState.IsValid || operationId == Guid.Empty || obligationIds is not { Length: >= 1 and <= 25 } ||
            obligationIds.Any(x => x == Guid.Empty) || obligationIds.Distinct().Count() != obligationIds.Length) return SelectionProblem();
        var account = (await billing.ReadAsync(false, accountId: accountId)).SingleOrDefault();
        if (account is null || obligationIds.Any(x => !account.Obligations.Any(o => o.Id == x))) return NotFound();
        var balances = await actions.ReviewAsync(accountId, obligationIds);
        if (balances.Count != obligationIds.Length || balances.Any(x => x.Held || x.HasProviderInstruction || x.RemainingMinorUnits <= 0 || x.Currency != "USD") ||
            balances.Count(x => x.RemainingMinorUnits >= 50) > 1) return ChangedProblem();
        var total = balances.Sum(x => x.RemainingMinorUnits);
        try { BillingRules.IssuedAmount(new Money(total)); } catch (ArgumentException) { return SelectionProblem(); }
        var actor = await current.GetAsync();
        if (actor is null || (await billing.ReadAsync(false, accountId: accountId)).SingleOrDefault() is null) return NotFound();
        return View("Review", tickets.Create(new(actor.UserId, time.GetUtcNow(), account.Suite, new(operationId, accountId, balances),
            balances.Select(x => new BillingReviewLine(account.Obligations.Single(o => o.Id == x.ObligationId).Description, BillingDisplay.Usd(x.RemainingMinorUnits) + " USD")).ToArray())));
    }
    [HttpPost, RequestSizeLimit(98304), EnableRateLimiting("PaymentActions")]
    public async Task<IActionResult> Confirm(string? ticket)
    {
        if (!policy.Enabled) return NotFound();
        var actor = await current.GetAsync(); if (actor is null) return NotFound();
        var review = tickets.Read(ticket, actor.UserId); if (review is null) return ProblemView(400, "Review the payment again",
            "This review is no longer available. Check recent payment requests and current balances before creating a new review.");
        var result = await actions.AdmitAsync(review.Command);
        return result.Code switch
        {
            CollectionCode.Success => RedirectToAction(nameof(Status), new { id = result.AttemptId }),
            CollectionCode.Conflict => ChangedProblem(),
            CollectionCode.ValidationFailed => SelectionProblem(),
            _ => NotFound()
        };
    }
    [HttpGet]
    public async Task<IActionResult> Status(Guid id)
    {
        var payment = await payments.ReadAsync(id);
        return payment is null ? NotFound() : View(payment);
    }
    [HttpPost, EnableRateLimiting("PaymentActions")]
    public async Task<IActionResult> Continue(Guid id)
    {
        if (!policy.Enabled || await payments.ReadAsync(id) is null) return NotFound();
        var uri = await payments.ContinueAsync(id);
        if (uri is not null) return Redirect(uri.AbsoluteUri);
        TempData["Notice"] = "Checkout is unavailable right now. Check the payment status before trying again.";
        return RedirectToAction(nameof(Status), new { id });
    }

    private ViewResult SelectionProblem() => ProblemView(400, "Choose an eligible payment", "Review the selected charges and their current balances. Choose one charge and eligible small balances with a combined amount of at least $0.50. Check recent requests before trying again.");
    private ViewResult ChangedProblem() => ProblemView(409, "Check the current balance", "Some selected items changed or are already being checked. Review recent payment requests and current balances before trying again. Contact your manager if an item remains unavailable.");
    private ViewResult ProblemView(int status, string heading, string message)
    {
        Response.StatusCode = status;
        return View("Problem", new PaymentReviewProblem(heading, message));
    }
}
