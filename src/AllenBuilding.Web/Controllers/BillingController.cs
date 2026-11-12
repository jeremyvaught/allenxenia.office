using AllenBuilding.Application.Access;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Web.Models;
using AllenBuilding.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AllenBuilding.Web.Controllers;

[Authorize, RequiresBilling, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class BillingController(
    BillingActions actions, ICurrentActor current, BillingReviewTickets tickets, IConfiguration configuration, TimeProvider time,
    CollectionPolicy collection, AutopayPolicy autopay, LocalCreditActions credits, BillingFeatures features) : Controller
{
    private bool Enabled => configuration.GetValue<bool>("Billing:PlanningEnabled");
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        if (!Enabled)
            return NotFound();
        var accounts = await actions.ReadAsync(false);
        // Nearly every tenant has one account, so skip the one-item list.
        if (accounts.Count == 1)
            return RedirectToAction(nameof(Account), new { id = accounts[0].Id });
        ViewData["Today"] = BillingDisplay.Today(time);
        return View(accounts);
    }
    // Stripe success and cancel share a status-only return; query parameters never settle money.
    [HttpGet]
    public IActionResult Return(Guid attemptId) => RedirectToAction("Status", "Payments", new { id = attemptId });
    [HttpGet]
    public async Task<IActionResult> Account(Guid id) => await AccountView(id, false);
    [Authorize(Policy = OfficeSecurity.ManagerPolicy), HttpGet]
    public async Task<IActionResult> Manage()
    {
        if (!Enabled)
            return NotFound();
        ViewData["Today"] = BillingDisplay.Today(time);
        ViewData["FinancialReview"] = features.FinancialReview;
        return View(new BillingManagement(await actions.ReadAsync(true), await actions.CandidatesAsync()));
    }
    [Authorize(Policy = OfficeSecurity.ManagerPolicy), RequiresFinancialReview, HttpGet]
    public async Task<IActionResult> Closures()
    {
        if (!Enabled)
            return NotFound();
        ViewData["FinancialReview"] = features.FinancialReview;
        return View(await actions.ClosuresAsync());
    }
    [Authorize(Policy = OfficeSecurity.ManagerPolicy), HttpGet]
    public async Task<IActionResult> ManageAccount(Guid id) => await AccountView(id, true);

    [Authorize(Policy = OfficeSecurity.RecentManagerPolicy), HttpPost]
    public async Task<IActionResult> PrepareOpen(OpenAccountForm form)
    {
        if (!Enabled)
            return NotFound();
        if (!ModelState.IsValid || form.MembershipId == Guid.Empty || form.OperationId == Guid.Empty || form.AccountId == Guid.Empty)
            return Failure(BillingCode.ValidationFailed);
        var candidate = (await actions.CandidatesAsync()).SingleOrDefault(x => x.MembershipId == form.MembershipId);
        if (candidate is null)
            return NotFound();
        return await Review($"Suite {candidate.Suite} · {candidate.Name ?? candidate.Email}", "Open a rent account",
            [new("Tenant", candidate.Name is null ? candidate.Email : $"{candidate.Name} ({candidate.Email})"), new("Suite", candidate.Suite),
             new("What happens", "A rent account is opened for this tenant. No rent terms or charges are added yet.")],
            open: new(form.OperationId, form.AccountId, candidate.MembershipId));
    }

    [Authorize(Policy = OfficeSecurity.RecentManagerPolicy), HttpPost]
    public async Task<IActionResult> PrepareTerms(RentTermsForm form)
    {
        if (!Enabled)
            return NotFound();
        if (!ModelState.IsValid || form.OperationId == Guid.Empty)
            return Failure(BillingCode.ValidationFailed);
        var account = (await actions.ReadAsync(true, accountId: form.AccountId)).SingleOrDefault();
        if (account is null)
            return NotFound();
        if (account.Version != form.Version)
            return Failure(BillingCode.Conflict);
        SetRentTerms command;
        try
        {
            command = new(form.OperationId, account.Id, account.Version, BillingDisplay.MinorUnits(form.Amount!.Value), form.DueDay,
                form.StartsOn!.Value, form.EndsOn, form.ReplacesId);
            RentAgreement.Create(account.Id, new Money(command.AmountMinorUnits), command.DueDay, command.StartsOn, command.EndsOn, "review", time.GetUtcNow());
            if (command.ReplacesId is not null && (command.ReplacesId == Guid.Empty || command.StartsOn.Day != 1))
                throw new ArgumentException();
        }
        catch (ArgumentException) { return Failure(BillingCode.ValidationFailed); }
        if (command.ReplacesId is { } priorId && !account.Terms.Any(x => x.Id == priorId && x.SupersededBy == null))
            return Failure(BillingCode.Conflict);
        return await Review(BillingDisplay.Label(account), command.ReplacesId is null ? "Set rent terms" : "Change rent terms",
            [new("Monthly rent", BillingDisplay.Usd(command.AmountMinorUnits)),
             new("Due each month on", $"the {BillingDisplay.Ordinal(command.DueDay)}, or the last day of a shorter month"),
             new("Starts", BillingDisplay.Day(command.StartsOn)), new("Ends", command.EndsOn is { } ends ? "before " + BillingDisplay.Day(ends) : "No end date"),
             new("Replaces", command.ReplacesId is { } replaced && account.Terms.SingleOrDefault(x => x.Id == replaced) is { } old
                 ? $"{BillingDisplay.Usd(old.AmountMinorUnits)} a month from {BillingDisplay.Day(old.StartsOn)}" : "Nothing"),
             new("What happens", "These terms apply to rent charged for whole months from the start date. Charges already added keep their amount and due date.")],
            terms: command);
    }

    [Authorize(Policy = OfficeSecurity.RecentManagerPolicy), HttpPost]
    public async Task<IActionResult> PrepareCharge(ChargeForm form)
    {
        if (!Enabled)
            return NotFound();
        if (!ModelState.IsValid || form.OperationId == Guid.Empty)
            return Failure(BillingCode.ValidationFailed);
        var account = (await actions.ReadAsync(true, accountId: form.AccountId)).SingleOrDefault();
        if (account is null)
            return NotFound();
        if (account.Version != form.Version)
            return Failure(BillingCode.Conflict);
        IssueCharge command;
        try
        {
            command = new(form.OperationId, account.Id, account.Version, BillingDisplay.MinorUnits(form.Amount!.Value),
                form.Description.Trim(), form.DueOn!.Value);
            Obligation.Issue(account.Id, new Money(command.AmountMinorUnits), command.Description, command.DueOn, "review", time.GetUtcNow());
        }
        catch (ArgumentException) { return Failure(BillingCode.ValidationFailed); }
        return await Review(BillingDisplay.Label(account), "Add a one-time charge",
            [new("Amount", BillingDisplay.Usd(command.AmountMinorUnits)), new("Description", command.Description),
             new("Due", BillingDisplay.Day(command.DueOn)),
             new("What happens", "The charge is added to this tenant's account. No payment is taken.")], charge: command);
    }

    [Authorize(Policy = OfficeSecurity.RecentManagerPolicy), HttpPost]
    public async Task<IActionResult> PrepareRent(RentPeriodForm form)
    {
        if (!Enabled)
            return NotFound();
        if (!ModelState.IsValid || form.OperationId == Guid.Empty)
            return Failure(BillingCode.ValidationFailed);
        var account = (await actions.ReadAsync(true, accountId: form.AccountId)).SingleOrDefault();
        if (account is null)
            return NotFound();
        if (account.Version != form.Version)
            return Failure(BillingCode.Conflict);
        var preview = await actions.PreviewRentAsync(account.Id, form.Year, form.Month);
        if (preview is null)
            return Failure(BillingCode.NoFullPeriod);
        if (preview.AccountVersion != account.Version)
            return Failure(BillingCode.Conflict);
        var monthName = preview.StartsOn.ToString("MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        return await Review(BillingDisplay.Label(account), preview.AlreadyIssued ? "Rent already charged for " + monthName : "Charge rent for " + monthName,
            [new("Amount", BillingDisplay.Usd(preview.AmountMinorUnits)), new("Month", monthName),
             new("Due", BillingDisplay.Day(preview.DueOn)),
             new("What happens", preview.AlreadyIssued
                 ? "Rent for this month is already on the account. Confirming adds nothing; a month is never charged twice."
                 : "One rent charge for this month is added to the account. No payment is taken.")],
            rent: new(form.OperationId, account.Id, account.Version, form.Year, form.Month));
    }

    [Authorize(Policy = OfficeSecurity.RecentManagerPolicy), HttpPost]
    public async Task<IActionResult> PrepareEnd(EndTenancy command)
    {
        if (!Enabled)
            return NotFound();
        if (!ModelState.IsValid || command.OperationId == Guid.Empty || !TenancyClosure.ValidReason(command.Reason))
            return Failure(BillingCode.ValidationFailed);
        var account = (await actions.ReadAsync(true, accountId: command.AccountId)).SingleOrDefault();
        if (account is null)
            return NotFound();
        if (account.Version != command.Version || account.MembershipVersion != command.MembershipVersion || account.EndedAt is not null)
            return Failure(BillingCode.Conflict);
        var actor = await current.GetAsync();
        if (actor is not { ChallengedMfa: true, RecentMfa: true })
            return Forbid();
        return View("Review", tickets.Create(new(actor.UserId, time.GetUtcNow(), BillingDisplay.Label(account), "End this tenancy",
            [new("Portal access", "Removed as soon as you confirm. Any other suite this person has is not affected."),
             new("Billing", "No new charges or rent terms can be added to this account. What is already owed, credited or paid stays on record."),
             new("Reason", command.Reason.Trim())], End: command with { Reason = command.Reason.Trim() })));
    }

    [Authorize(Policy = OfficeSecurity.RecentManagerPolicy), HttpPost]
    public async Task<IActionResult> PrepareCredit(CreditForm form)
    {
        if (!Enabled)
            return NotFound();
        if (!ModelState.IsValid || form.OperationId == Guid.Empty || form.ObligationId == Guid.Empty || !Enum.IsDefined(form.Kind))
            return Failure(BillingCode.ValidationFailed);
        var account = (await actions.ReadAsync(true, accountId: form.AccountId)).SingleOrDefault();
        var charge = account?.Obligations.SingleOrDefault(x => x.Id == form.ObligationId);
        if (account is null || charge is null)
            return NotFound();
        if (charge.Version != form.Version)
            return Failure(BillingCode.Conflict);
        long amount;
        try
        { amount = BillingDisplay.MinorUnits(form.Amount!.Value, 1); }
        catch (ArgumentException) { return Failure(BillingCode.ValidationFailed); }
        if (amount > charge.BalanceMinorUnits)
            return Failure(BillingCode.ValidationFailed);
        var writeOff = form.Kind == ObligationCreditKind.WriteOff;
        return await Review(BillingDisplay.Label(account), writeOff ? "Write off part of a charge" : "Record a credit",
            [new("Charge", $"{BillingDisplay.Charge(charge.Description)}, due {BillingDisplay.Day(charge.DueOn)}"),
             new(writeOff ? "Amount written off" : "Amount credited", BillingDisplay.Usd(amount)),
             new("Still owed on this charge afterwards", BillingDisplay.Usd(charge.BalanceMinorUnits - amount)),
             new("Reason", form.Reason.Trim()),
             new("What happens", "The amount owed on this charge goes down. This records a decision by management; it does not take or verify a payment, and it cannot be undone here.")],
            credit: new(form.OperationId, account.Id, charge.Id, charge.Version, amount, form.Kind, form.Reason.Trim()));
    }

    [Authorize(Policy = OfficeSecurity.RecentManagerPolicy), HttpPost]
    public async Task<IActionResult> Confirm(string? ticket)
    {
        if (!Enabled)
            return NotFound();
        var actor = await current.GetAsync();
        if (actor is not { ChallengedMfa: true, RecentMfa: true })
            return Forbid();
        var review = tickets.Read(ticket, actor.UserId);
        if (review is null)
            return Failure(BillingCode.ValidationFailed);
        var result = review switch
        {
            { End: { } command } => await actions.EndAsync(command),
            { Open: { } command } => await actions.OpenAsync(command),
            { Terms: { } command } => await actions.TermsAsync(command),
            { Charge: { } command } => await actions.ChargeAsync(command),
            { Rent: { } command } => await actions.RentAsync(command),
            { Credit: { } command } => await credits.ApplyAsync(command),
            _ => new(BillingCode.ValidationFailed)
        };
        if (result.Code != BillingCode.Success)
            return Failure(result.Code);
        TempData["Notice"] = review switch
        {
            { End: not null } => "Tenancy ended. Portal access is removed and no new charges can be added.",
            { Open: not null } => "Rent account opened. Set the rent terms next.",
            { Terms: not null } => "Rent terms saved.",
            { Charge: not null } => "Charge added.",
            { Rent: not null } => "Rent charged.",
            { Credit.Kind: ObligationCreditKind.WriteOff } => "Write-off recorded.",
            _ => "Credit recorded."
        };
        return RedirectToAction(nameof(ManageAccount), new { id = result.AccountId });
    }

    private async Task<IActionResult> AccountView(Guid id, bool management)
    {
        if (!Enabled)
            return NotFound();
        var account = (await actions.ReadAsync(management, accountId: id)).SingleOrDefault();
        if (account is null)
            return NotFound();
        ViewData["Manager"] = management;
        ViewData["AutopayAvailable"] = !management && autopay.Enabled;
        ViewData["Today"] = BillingDisplay.Today(time);
        ViewData["FinancialReview"] = features.FinancialReview;
        return View("Account", account with { CollectionAvailable = !management && collection.Enabled });
    }
    private async Task<IActionResult> Review(string account, string heading, IReadOnlyList<BillingReviewLine> lines,
        OpenRentAccount? open = null, SetRentTerms? terms = null, IssueCharge? charge = null, IssueRent? rent = null, CreditLocalDebt? credit = null)
    {
        var actor = await current.GetAsync();
        if (actor is not { ChallengedMfa: true, RecentMfa: true })
            return Forbid();
        return View("Review", tickets.Create(new(actor.UserId, time.GetUtcNow(), account, heading, lines, open, terms, charge, rent, Credit: credit)));
    }
    private IActionResult Failure(BillingCode code)
    {
        if (code == BillingCode.Unavailable)
            return NotFound();
        Response.StatusCode = code == BillingCode.Conflict ? 409 : 400;
        return View("Error", code);
    }
}
