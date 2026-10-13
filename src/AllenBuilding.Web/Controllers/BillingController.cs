using AllenBuilding.Application.Access;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Web.Models;
using AllenBuilding.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AllenBuilding.Web.Controllers;

[Authorize, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class BillingController(BillingActions actions, ICurrentActor current, BillingReviewTickets tickets, IConfiguration configuration, TimeProvider time, CollectionPolicy collection) : Controller
{
    private bool Enabled => configuration.GetValue<bool>("Billing:PlanningEnabled");
    [HttpGet]
    public async Task<IActionResult> Index() => Enabled ? View(await actions.ReadAsync(false)) : NotFound();
    // Stripe success and cancel share a status-only return; query parameters never settle money.
    [HttpGet]
    public IActionResult Return(Guid attemptId) => RedirectToAction("Status", "Payments", new { id = attemptId });
    [HttpGet]
    public async Task<IActionResult> Account(Guid id) => await AccountView(id, false);
    [Authorize(Policy = OfficeSecurity.ManagerPolicy), HttpGet]
    public async Task<IActionResult> Manage() => Enabled ? View(new BillingManagement(await actions.ReadAsync(true), await actions.CandidatesAsync())) : NotFound();
    [Authorize(Policy = OfficeSecurity.ManagerPolicy), HttpGet]
    public async Task<IActionResult> ManageAccount(Guid id) => await AccountView(id, true);

    [Authorize(Policy = OfficeSecurity.RecentManagerPolicy), HttpPost]
    public async Task<IActionResult> PrepareOpen(OpenAccountForm form)
    {
        if (!Enabled) return NotFound();
        if (!ModelState.IsValid || form.MembershipId == Guid.Empty || form.OperationId == Guid.Empty || form.AccountId == Guid.Empty) return Failure(BillingCode.ValidationFailed);
        var candidate = (await actions.CandidatesAsync()).SingleOrDefault(x => x.MembershipId == form.MembershipId);
        if (candidate is null) return NotFound();
        return await Review($"{candidate.Suite} · {candidate.Email} · membership {candidate.MembershipId:D}", "Open rent account",
            [new("Original membership", candidate.MembershipId.ToString("D")), new("Effect", "Create an account for this membership. No rent terms or charges are created.")],
            open: new(form.OperationId, form.AccountId, candidate.MembershipId));
    }

    [Authorize(Policy = OfficeSecurity.RecentManagerPolicy), HttpPost]
    public async Task<IActionResult> PrepareTerms(RentTermsForm form)
    {
        if (!Enabled) return NotFound();
        if (!ModelState.IsValid || form.OperationId == Guid.Empty) return Failure(BillingCode.ValidationFailed);
        var account = (await actions.ReadAsync(true, accountId: form.AccountId)).SingleOrDefault();
        if (account is null) return NotFound();
        if (account.Version != form.Version) return Failure(BillingCode.Conflict);
        SetRentTerms command;
        try
        {
            command = new(form.OperationId, account.Id, account.Version, BillingDisplay.MinorUnits(form.Amount!.Value), form.DueDay, form.StartsOn!.Value, form.EndsOn, form.ReplacesId);
            RentAgreement.Create(account.Id, new Money(command.AmountMinorUnits), command.DueDay, command.StartsOn, command.EndsOn, "review", time.GetUtcNow());
            if (command.ReplacesId is not null && (command.ReplacesId == Guid.Empty || command.StartsOn.Day != 1)) throw new ArgumentException();
        }
        catch (ArgumentException) { return Failure(BillingCode.ValidationFailed); }
        if (command.ReplacesId is { } priorId && !account.Terms.Any(x => x.Id == priorId && x.SupersededBy == null)) return Failure(BillingCode.Conflict);
        return await Review(BillingDisplay.Label(account), command.ReplacesId is null ? "Set calendar rent terms" : "Replace calendar rent terms",
            [new("Monthly rent", BillingDisplay.Usd(command.AmountMinorUnits) + " USD"), new("Due day", $"{command.DueDay}; clamps to the last day of shorter months"),
             new("Starts on", command.StartsOn.ToString("yyyy-MM-dd")), new("Ends before", command.EndsOn?.ToString("yyyy-MM-dd") ?? "No end date"),
             new("Replaces agreement", command.ReplacesId?.ToString("D") ?? "None"), new("Effect", "Whole calendar months only. Already-issued amounts and due dates stay unchanged. This does not enroll autopay or collect a payment.")], terms: command);
    }

    [Authorize(Policy = OfficeSecurity.RecentManagerPolicy), HttpPost]
    public async Task<IActionResult> PrepareCharge(ChargeForm form)
    {
        if (!Enabled) return NotFound();
        if (!ModelState.IsValid || form.OperationId == Guid.Empty) return Failure(BillingCode.ValidationFailed);
        var account = (await actions.ReadAsync(true, accountId: form.AccountId)).SingleOrDefault();
        if (account is null) return NotFound();
        if (account.Version != form.Version) return Failure(BillingCode.Conflict);
        IssueCharge command;
        try
        {
            command = new(form.OperationId, account.Id, account.Version, BillingDisplay.MinorUnits(form.Amount!.Value), form.Description.Trim(), form.DueOn!.Value);
            Obligation.Issue(account.Id, new Money(command.AmountMinorUnits), command.Description, command.DueOn, "review", time.GetUtcNow());
        }
        catch (ArgumentException) { return Failure(BillingCode.ValidationFailed); }
        return await Review(BillingDisplay.Label(account), "Issue one-time charge",
            [new("Issued amount", BillingDisplay.Usd(command.AmountMinorUnits) + " USD"), new("Description", command.Description), new("Due on", command.DueOn.ToString("yyyy-MM-dd")),
             new("Effect", "Record this obligation. No payment is collected.")], charge: command);
    }

    [Authorize(Policy = OfficeSecurity.RecentManagerPolicy), HttpPost]
    public async Task<IActionResult> PrepareRent(RentPeriodForm form)
    {
        if (!Enabled) return NotFound();
        if (!ModelState.IsValid || form.OperationId == Guid.Empty) return Failure(BillingCode.ValidationFailed);
        var account = (await actions.ReadAsync(true, accountId: form.AccountId)).SingleOrDefault();
        if (account is null) return NotFound();
        if (account.Version != form.Version) return Failure(BillingCode.Conflict);
        var preview = await actions.PreviewRentAsync(account.Id, form.Year, form.Month);
        if (preview is null) return Failure(BillingCode.NoFullPeriod);
        if (preview.AccountVersion != account.Version) return Failure(BillingCode.Conflict);
        return await Review(BillingDisplay.Label(account), preview.AlreadyIssued ? "Review already-issued rent period" : "Issue calendar rent",
            [new("Issued amount", BillingDisplay.Usd(preview.AmountMinorUnits) + " USD"), new("Period starts", preview.StartsOn.ToString("yyyy-MM-dd")), new("Period ends before", preview.EndsOn.ToString("yyyy-MM-dd")),
             new("Due on", preview.DueOn.ToString("yyyy-MM-dd")), new("Effect", preview.AlreadyIssued ? "Return the existing obligation. This cannot charge the same period again." : "Record one rent obligation for this whole month. No payment is collected.")],
            rent: new(form.OperationId, account.Id, account.Version, form.Year, form.Month));
    }

    [Authorize(Policy = OfficeSecurity.RecentManagerPolicy), HttpPost]
    public async Task<IActionResult> Confirm(string? ticket)
    {
        if (!Enabled) return NotFound();
        var actor = await current.GetAsync();
        if (actor is not { ChallengedMfa: true, RecentMfa: true }) return Forbid();
        var review = tickets.Read(ticket, actor.UserId);
        if (review is null) return Failure(BillingCode.ValidationFailed);
        var result = review switch
        {
            { Open: { } command } => await actions.OpenAsync(command),
            { Terms: { } command } => await actions.TermsAsync(command),
            { Charge: { } command } => await actions.ChargeAsync(command),
            { Rent: { } command } => await actions.RentAsync(command),
            _ => new(BillingCode.ValidationFailed)
        };
        if (result.Code != BillingCode.Success) return Failure(result.Code);
        TempData["Notice"] = "Reviewed billing change recorded. Payment collection remains unavailable.";
        return RedirectToAction(nameof(ManageAccount), new { id = result.AccountId });
    }

    private async Task<IActionResult> AccountView(Guid id, bool management)
    {
        if (!Enabled) return NotFound();
        var account = (await actions.ReadAsync(management, accountId: id)).SingleOrDefault();
        if (account is null) return NotFound();
        ViewData["Manager"] = management;
        ViewData["Today"] = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        return View("Account", account with { CollectionAvailable = !management && collection.Enabled });
    }
    private async Task<IActionResult> Review(string account, string heading, IReadOnlyList<BillingReviewLine> lines,
        OpenRentAccount? open = null, SetRentTerms? terms = null, IssueCharge? charge = null, IssueRent? rent = null)
    {
        var actor = await current.GetAsync();
        if (actor is not { ChallengedMfa: true, RecentMfa: true }) return Forbid();
        return View("Review", tickets.Create(new(actor.UserId, time.GetUtcNow(), account, heading, lines, open, terms, charge, rent)));
    }
    private IActionResult Failure(BillingCode code)
    {
        if (code == BillingCode.Unavailable) return NotFound();
        Response.StatusCode = code == BillingCode.Conflict ? 409 : 400;
        return View("Error", code);
    }
}
