using System.Security.Claims;
using AllenBuilding.Application.Access;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Web.Models;
using AllenBuilding.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace AllenBuilding.Web.Controllers;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("Accounts")]
public sealed class AccountController(UserManager<OfficeUser> users, SignInManager<OfficeUser> signIn,
    TimeProvider time, AccountSecurity security, OperatorEnrollment operatorEnrollment, ICurrentActor current) : Controller
{
    [HttpGet]
    public IActionResult Login(string? returnUrl)
    {
        // Tag helpers otherwise prefer the raw bound query over the sanitized model.
        ModelState.Remove(nameof(LoginForm.ReturnUrl));
        return View(new LoginForm { ReturnUrl = SafeReturn(returnUrl) });
    }

    [HttpPost]
    public async Task<IActionResult> Login(LoginForm form)
    {
        form.ReturnUrl = SafeReturn(form.ReturnUrl);
        ModelState.Remove(nameof(form.ReturnUrl));
        if (!ModelState.IsValid)
            return View(form);
        await signIn.SignOutAsync();
        await signIn.ForgetTwoFactorClientAsync();
        var result = await signIn.PasswordSignInAsync(form.Email, form.Password, false, lockoutOnFailure: true);
        if (result.RequiresTwoFactor)
            return RedirectToAction(nameof(Factor), form.ReturnUrl is null ? null : new { returnUrl = form.ReturnUrl });
        if (result.Succeeded)
        {
            var user = (await users.FindByNameAsync(form.Email))!;
            await signIn.SignInWithClaimsAsync(user, false, [OfficeSecurity.TimeClaim(OfficeSecurity.PasswordTime, time.GetUtcNow())]);
            // First sign-in is a welcome, not an enrollment wizard. Protected manager
            // destinations still enforce their policies when selected afterward.
            if (!user.TwoFactorEnabled && !user.MustReplaceRecoveryCodes &&
                await security.IsManagerAsync(user.Id, HttpContext.RequestAborted) &&
                form.ReturnUrl != "/Account/Enroll")
                return RedirectToAction("Index", "Portal");
            return AfterSignIn(form.ReturnUrl);
        }
        ModelState.AddModelError("", "Unable to sign in. Check your details or try again later.");
        return View(form);
    }

    [HttpGet]
    public async Task<IActionResult> Factor(string? returnUrl)
    {
        ModelState.Remove(nameof(FactorForm.ReturnUrl));
        return await signIn.GetTwoFactorAuthenticationUserAsync() is null
            ? LoginAgain(returnUrl) : View(new FactorForm { ReturnUrl = SafeReturn(returnUrl) });
    }

    [HttpPost]
    public async Task<IActionResult> Factor(FactorForm form)
    {
        form.ReturnUrl = SafeReturn(form.ReturnUrl);
        ModelState.Remove(nameof(form.ReturnUrl));
        var challenge = await HttpContext.AuthenticateAsync(IdentityConstants.TwoFactorUserIdScheme);
        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null || challenge.Properties?.IssuedUtc is not { } passwordAt)
            return LoginAgain(form.ReturnUrl);
        if (!ModelState.IsValid)
            return View(form);
        var result = form.Recovery ? await signIn.TwoFactorRecoveryCodeSignInAsync(form.Code.Trim())
            : await signIn.TwoFactorAuthenticatorSignInAsync(NormalizeCode(form.Code), false, false);
        if (result.Succeeded)
        {
            if (form.Recovery)
                await security.RecordRecoveryCodeUseAsync(user.Id);
            await ChallengedSignIn(user, passwordAt, form.Recovery);
            return form.Recovery || (form.ReturnUrl is not null && user.MustReplaceRecoveryCodes)
                ? RedirectToAction(nameof(RecoveryCodes)) : AfterSignIn(form.ReturnUrl);
        }
        ModelState.AddModelError("", "Unable to verify this code. Try again or sign in again.");
        return View(form);
    }

    [Authorize, HttpGet]
    public async Task<IActionResult> Enroll()
    {
        var user = (await users.GetUserAsync(User))!;
        if (user.TwoFactorEnabled)
            return RedirectToAction(nameof(Login));
        if (!await EnrollmentAllowed(user))
            return View("OperatorEnrollment", new OperatorEnrollmentForm());
        var firstSetup = !user.MustReplaceRecoveryCodes;
        if (firstSetup && !FreshEnrollmentPassword(out _))
            return LoginAgain("/Account/Enroll");
        var key = await users.GetAuthenticatorKeyAsync(user);
        if (key is null)
        {
            if (!(await users.ResetAuthenticatorKeyAsync(user)).Succeeded)
                return StatusCode(409);
            // Resetting the seed changes the stamp. Carry forward the actual password
            // proof, never turn a refresh into a new password verification.
            await signIn.SignInWithClaimsAsync(user, false, User.Claims.Where(x => x.Type is OfficeSecurity.PasswordTime or OperatorEnrollment.SessionTime));
            key = await users.GetAuthenticatorKeyAsync(user);
        }
        return View(new EnrollmentForm { Key = key, FirstSetup = firstSetup });
    }

    [Authorize, HttpPost]
    public async Task<IActionResult> Enroll(EnrollmentForm form)
    {
        var user = (await users.GetUserAsync(User))!;
        await using var transaction = await security.LockForEnrollmentAsync(user, HttpContext.RequestAborted);
        if (user.TwoFactorEnabled)
            return RedirectToAction(nameof(Login));
        if (!await EnrollmentAllowed(user))
            return StatusCode(403);
        var firstSetup = !user.MustReplaceRecoveryCodes;
        var passwordAt = default(DateTimeOffset);
        if (firstSetup && !FreshEnrollmentPassword(out passwordAt))
            return LoginAgain("/Account/Enroll");
        if (ModelState.IsValid && !await users.IsLockedOutAsync(user) &&
            await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, NormalizeCode(form.Code)))
        {
            var enrolled = await security.CompleteEnrollmentAsync(user, firstSetup);
            if (!enrolled.Succeeded)
                return StatusCode(409);
            await transaction.CommitAsync();
            if (enrolled.RecoveryCodes is { } codes)
            {
                await ChallengedSignIn(user, passwordAt);
                return View("RecoveryIssued", codes);
            }
            // Recovery enrollment still requires a new password + factor challenge.
            await signIn.SignOutAsync();
            return RedirectToAction(nameof(Login));
        }
        await users.AccessFailedAsync(user);
        await transaction.CommitAsync();
        ModelState.AddModelError("", "Unable to verify this code. Check your authenticator and try again.");
        form.Key = await users.GetAuthenticatorKeyAsync(user);
        form.FirstSetup = firstSetup;
        return View(form);
    }

    [Authorize, HttpPost]
    public async Task<IActionResult> AuthorizeEnrollment(OperatorEnrollmentForm form)
    {
        var user = (await users.GetUserAsync(User))!;
        if (!ModelState.IsValid || user.SecurityStamp is null ||
            !await operatorEnrollment.AuthorizeAsync(user.Id, user.SecurityStamp, form.Code.Trim(), HttpContext.RequestAborted))
        {
            ModelState.Remove(nameof(form.Code));
            form.Code = "";
            ModelState.AddModelError("", "Unable to verify this recovery code. Ask the verified operator for a new code if it expired or was already used.");
            Response.StatusCode = 400;
            return View("OperatorEnrollment", form);
        }
        await security.ReloadAsync(user, HttpContext.RequestAborted);
        await signIn.SignInWithClaimsAsync(user, false, [OfficeSecurity.TimeClaim(OperatorEnrollment.SessionTime, time.GetUtcNow())]);
        return RedirectToAction(nameof(Enroll));
    }

    private async Task<bool> EnrollmentAllowed(OfficeUser user)
    {
        if (User.FindFirstValue(users.Options.ClaimsIdentity.SecurityStampClaimType) != user.SecurityStamp)
            return false;
        if (await operatorEnrollment.RequiredAsync(user) != "true")
            return true;
        return long.TryParse(User.FindFirstValue(OperatorEnrollment.SessionTime), out var at) &&
            time.GetUtcNow().ToUnixTimeSeconds() - at is >= 0 and <= 600;
    }

    [Authorize, HttpGet]
    public async Task<IActionResult> RecoveryCodes()
    {
        var user = (await users.GetUserAsync(User))!;
        if (!User.HasClaim("amr", "mfa") || !user.TwoFactorEnabled)
            return Forbid();
        return View("RecoveryReview");
    }

    [Authorize, HttpPost]
    public async Task<IActionResult> RecoverAuthenticator(RecoverAuthenticatorForm form)
    {
        var user = (await users.GetUserAsync(User))!;
        if (!User.HasClaim("office.recovery_session", "true") || !user.MustReplaceRecoveryCodes ||
            !long.TryParse(User.FindFirstValue(OfficeSecurity.FactorTime), out var verified) ||
            time.GetUtcNow().ToUnixTimeSeconds() - verified is < 0 or > 300)
            return Forbid();
        if (!ModelState.IsValid || !(await signIn.CheckPasswordSignInAsync(user, form.Password, true)).Succeeded)
        { ModelState.AddModelError("", "Unable to verify your password."); return View("RecoveryReview", new ReauthenticateForm()); }
        if (!await security.ResetAuthenticatorAsync(user))
            return StatusCode(409);
        await signIn.SignOutAsync();
        return RedirectToAction(nameof(Login));
    }

    [Authorize, HttpPost]
    public async Task<IActionResult> RecoveryCodes(ReauthenticateForm form)
    {
        var user = (await users.GetUserAsync(User))!;
        if (!User.HasClaim("amr", "mfa") || !user.TwoFactorEnabled)
            return Forbid();
        if (!ModelState.IsValid || !await VerifyPasswordAndFactor(user, form))
        { ModelState.AddModelError("", "Unable to verify your password and code."); return View("RecoveryReview", form); }
        var codes = await security.ReplaceRecoveryCodesAsync(user);
        if (codes is null)
            return StatusCode(409);
        await ChallengedSignIn(user, time.GetUtcNow());
        return View("RecoveryIssued", codes);
    }

    [Authorize, HttpGet] public IActionResult Reauthenticate() => View(new ReauthenticateForm());

    [Authorize, HttpPost]
    public async Task<IActionResult> Reauthenticate(ReauthenticateForm form)
    {
        var user = (await users.GetUserAsync(User))!;
        if (ModelState.IsValid && user.TwoFactorEnabled && await VerifyPasswordAndFactor(user, form))
        { await ChallengedSignIn(user, time.GetUtcNow()); return RedirectToAction("Index", "Portal"); }
        ModelState.AddModelError("", "Unable to verify your password and code.");
        return View(form);
    }

    [Authorize, HttpPost]
    public async Task<IActionResult> Logout()
    {
        await signIn.SignOutAsync();
        Response.Cookies.Delete(AssistantController.GuestHintCookie);
        Response.Cookies.Delete("office.assistant", new CookieOptions { Path = "/Assistant" });
        return RedirectToAction("Index", "Home");
    }
    [HttpGet]
    public async Task<IActionResult> Denied()
    {
        var actor = await current.GetAsync(HttpContext.RequestAborted);
        ViewData["CanReauthenticate"] = actor is { ChallengedMfa: true, RecentMfa: false } && actor.ManagedBuildings.Count > 0;
        return View();
    }

    // Navigation is not authorization or confirmation. The destination's GET handler checks
    // current access; no POST is replayed after sign-in, and recovery always retains its gate.
    private string? SafeReturn(string? path) => path is { Length: > 0 and <= 2048 } && path[0] == '/' &&
        !path.Any(char.IsControl) && !path.Contains('\\') && Url.IsLocalUrl(path) ? path : null;
    private IActionResult LoginAgain(string? path) =>
        RedirectToAction(nameof(Login), SafeReturn(path) is { } destination ? new { returnUrl = destination } : null);
    private IActionResult AfterSignIn(string? path) => SafeReturn(path) is { } destination
        ? LocalRedirect(destination) : RedirectToAction("Index", "Portal");

    private async Task<bool> VerifyPasswordAndFactor(OfficeUser user, ReauthenticateForm form)
    {
        if (!(await signIn.CheckPasswordSignInAsync(user, form.Password, true)).Succeeded)
            return false;
        if (await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, NormalizeCode(form.Code)))
            return true;
        await users.AccessFailedAsync(user);
        return false;
    }

    private Task ChallengedSignIn(OfficeUser user, DateTimeOffset passwordAt, bool recovery = false) => signIn.SignInWithClaimsAsync(user, false,
        new[] { new Claim("amr", "mfa"), new Claim("office.recovery_session", recovery ? "true" : "false"),
            OfficeSecurity.TimeClaim(OfficeSecurity.PasswordTime, passwordAt), OfficeSecurity.TimeClaim(OfficeSecurity.FactorTime, time.GetUtcNow()) });

    private static string NormalizeCode(string code) => code.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);

    private bool FreshEnrollmentPassword(out DateTimeOffset verified)
    {
        verified = default;
        if (!long.TryParse(User.FindFirstValue(OfficeSecurity.PasswordTime), out var seconds) ||
            time.GetUtcNow().ToUnixTimeSeconds() - seconds is < 0 or > 600)
            return false;
        verified = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return true;
    }
}
