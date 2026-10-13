using System.Security.Claims;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Web.Models;
using AllenBuilding.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Web.Controllers;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("Accounts")]
public sealed class AccountController(UserManager<OfficeUser> users, SignInManager<OfficeUser> signIn,
    TimeProvider time, AllenBuilding.Infrastructure.Persistence.OfficeDbContext db, AccountInvitations invitations) : Controller
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
        if (!ModelState.IsValid) return View(form);
        await signIn.SignOutAsync();
        await signIn.ForgetTwoFactorClientAsync();
        var result = await signIn.PasswordSignInAsync(form.Email, form.Password, false, lockoutOnFailure: true);
        if (result.RequiresTwoFactor) return RedirectToAction(nameof(Factor), form.ReturnUrl is null ? null : new { returnUrl = form.ReturnUrl });
        if (result.Succeeded)
        {
            // Preserve mandatory first-manager setup when skipping the dashboard.
            if (form.ReturnUrl is not null)
            {
                var user = await users.FindByNameAsync(form.Email);
                if (user is { TwoFactorEnabled: false } && await db.BuildingManagers.AnyAsync(x => x.UserId == user.Id, HttpContext.RequestAborted))
                    return RedirectToAction(nameof(Enroll));
            }
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
        if (user is null || challenge.Properties?.IssuedUtc is not { } passwordAt) return LoginAgain(form.ReturnUrl);
        if (!ModelState.IsValid) return View(form);
        var result = form.Recovery ? await signIn.TwoFactorRecoveryCodeSignInAsync(form.Code.Trim())
            : await signIn.TwoFactorAuthenticatorSignInAsync(NormalizeCode(form.Code), false, false);
        if (result.Succeeded)
        {
            if (form.Recovery)
            {
                invitations.Audit(user.Id, "RecoveryCodeUsed", user.Id);
                invitations.Queue(user.Id, "SecurityNotice", "A recovery code was used to sign in. Replace your recovery codes.");
                await db.SaveChangesAsync();
            }
            await ChallengedSignIn(user, passwordAt, form.Recovery);
            return form.Recovery || (form.ReturnUrl is not null && user.MustReplaceRecoveryCodes) ? RedirectToAction(nameof(RecoveryCodes)) : AfterSignIn(form.ReturnUrl);
        }
        ModelState.AddModelError("", "Unable to verify this code. Try again or sign in again.");
        return View(form);
    }

    [Authorize, HttpGet]
    public async Task<IActionResult> Enroll()
    {
        var user = (await users.GetUserAsync(User))!;
        if (user.TwoFactorEnabled) return RedirectToAction(nameof(Login));
        var key = await users.GetAuthenticatorKeyAsync(user);
        if (key is null)
        {
            if (!(await users.ResetAuthenticatorKeyAsync(user)).Succeeded) return StatusCode(409);
            await signIn.RefreshSignInAsync(user);
            key = await users.GetAuthenticatorKeyAsync(user);
        }
        return View(new EnrollmentForm { Key = key });
    }

    [Authorize, HttpPost]
    public async Task<IActionResult> Enroll(EnrollmentForm form)
    {
        var user = (await users.GetUserAsync(User))!;
        if (user.TwoFactorEnabled) return RedirectToAction(nameof(Login));
        if (ModelState.IsValid && !await users.IsLockedOutAsync(user) &&
            await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, NormalizeCode(form.Code)))
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            if (!(await users.SetTwoFactorEnabledAsync(user, true)).Succeeded) return StatusCode(409);
            user.MustReplaceRecoveryCodes = true;
            if (!(await users.UpdateAsync(user)).Succeeded) return StatusCode(409);
            invitations.Audit(user.Id, "MfaEnrolled", user.Id);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            // Enrollment does not grant management authority. Require a new password + factor challenge.
            await signIn.SignOutAsync();
            return RedirectToAction(nameof(Login));
        }
        await users.AccessFailedAsync(user);
        ModelState.AddModelError("", "Unable to verify this code. Check your authenticator and try again.");
        form.Key = await users.GetAuthenticatorKeyAsync(user);
        return View(form);
    }

    [Authorize, HttpGet]
    public async Task<IActionResult> RecoveryCodes()
    {
        var user = (await users.GetUserAsync(User))!;
        if (!User.HasClaim("amr", "mfa") || !user.TwoFactorEnabled) return Forbid();
        return View("RecoveryReview");
    }

    [Authorize, HttpPost]
    public async Task<IActionResult> RecoverAuthenticator(RecoverAuthenticatorForm form)
    {
        var user = (await users.GetUserAsync(User))!;
        if (!User.HasClaim("office.recovery_session", "true") || !user.MustReplaceRecoveryCodes ||
            !long.TryParse(User.FindFirstValue(OfficeSecurity.FactorTime), out var verified) ||
            time.GetUtcNow().ToUnixTimeSeconds() - verified is < 0 or > 300) return Forbid();
        if (!ModelState.IsValid || !(await signIn.CheckPasswordSignInAsync(user, form.Password, true)).Succeeded)
        { ModelState.AddModelError("", "Unable to verify your password."); return View("RecoveryReview", new ReauthenticateForm()); }
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (!(await users.SetTwoFactorEnabledAsync(user, false)).Succeeded ||
            !(await users.ResetAuthenticatorKeyAsync(user)).Succeeded ||
            (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 0)) is null) return StatusCode(409);
        invitations.Audit(user.Id, "AuthenticatorRecovered", user.Id);
        invitations.Queue(user.Id, "SecurityNotice", "Your authenticator was reset after recovery verification. Sign in and enroll a new device.");
        await db.SaveChangesAsync(); await transaction.CommitAsync();
        await signIn.SignOutAsync();
        return RedirectToAction(nameof(Login));
    }

    [Authorize, HttpPost]
    public async Task<IActionResult> RecoveryCodes(ReauthenticateForm form)
    {
        var user = (await users.GetUserAsync(User))!;
        if (!User.HasClaim("amr", "mfa") || !user.TwoFactorEnabled) return Forbid();
        if (!ModelState.IsValid || !await VerifyPasswordAndFactor(user, form))
        { ModelState.AddModelError("", "Unable to verify your password and code."); return View("RecoveryReview", form); }
        await using var transaction = await db.Database.BeginTransactionAsync();
        var codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))?.ToArray();
        if (codes is null) return StatusCode(409);
        user.MustReplaceRecoveryCodes = false;
        if (!(await users.UpdateAsync(user)).Succeeded) return StatusCode(409);
        invitations.Audit(user.Id, "RecoveryCodesReplaced", user.Id);
        invitations.Queue(user.Id, "SecurityNotice", "Recovery codes were replaced.");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
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
    [HttpGet] public IActionResult Denied() => View();

    // Navigation is not authorization or confirmation. The destination's GET handler checks
    // current access; no POST is replayed after sign-in, and recovery always retains its gate.
    private string? SafeReturn(string? path) => path is { Length: > 0 and <= 2048 } && path[0] == '/' &&
        !path.Any(char.IsControl) && !path.Contains('\\') && Url.IsLocalUrl(path) ? path : null;
    private IActionResult LoginAgain(string? path) => RedirectToAction(nameof(Login), SafeReturn(path) is { } destination ? new { returnUrl = destination } : null);
    private IActionResult AfterSignIn(string? path) => SafeReturn(path) is { } destination
        ? LocalRedirect(destination) : RedirectToAction("Index", "Portal");

    private async Task<bool> VerifyPasswordAndFactor(OfficeUser user, ReauthenticateForm form)
    {
        if (!(await signIn.CheckPasswordSignInAsync(user, form.Password, true)).Succeeded) return false;
        if (await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, NormalizeCode(form.Code))) return true;
        await users.AccessFailedAsync(user);
        return false;
    }

    private Task ChallengedSignIn(OfficeUser user, DateTimeOffset passwordAt, bool recovery = false) => signIn.SignInWithClaimsAsync(user, false,
        new[] { new Claim("amr", "mfa"), new Claim("office.recovery_session", recovery ? "true" : "false"),
            OfficeSecurity.TimeClaim(OfficeSecurity.PasswordTime, passwordAt), OfficeSecurity.TimeClaim(OfficeSecurity.FactorTime, time.GetUtcNow()) });

    private static string NormalizeCode(string code) => code.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
}
