using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stockia.Web.Domain;
using Stockia.Web.Security;
using Stockia.Web.Services;

namespace Stockia.Web.Controllers;

public class LoginViewModel
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string? Error { get; set; }
}

public class ChooseCompanyViewModel
{
    public int CompanyId { get; set; }
    public List<Assignment> Assignments { get; set; } = new();
}

public class AccountController : Controller
{
    private readonly LoginService _login;
    private readonly AssignmentService _assignments;
    private readonly CompanyAccessService _access;

    public AccountController(LoginService login, AssignmentService assignments, CompanyAccessService access)
    {
        _login = login;
        _assignments = assignments;
        _access = access;
    }

    [HttpGet, AllowAnonymous]
    public IActionResult Login() => View(new LoginViewModel());

    [HttpPost, AllowAnonymous, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        var user = await _login.ValidateAsync(model.Username, model.Password);
        if (user is null)
        {
            ModelState.Clear();
            model.Password = "";
            model.Error = "Invalid username or password.";
            return View(model);
        }

        var assignments = await _assignments.ForUserAsync(user.Id);
        if (assignments.Count == 1)
        {
            await SignInAsync(user, assignments[0].CompanyId);
            return RedirectToAction("Index", "Home");
        }

        await SignInAsync(user, null);
        return RedirectToAction(nameof(ChooseCompany));
    }

    // SR-33: a user with several assignments chooses the company for this session.
    [HttpGet, AllowNoCompany]
    public async Task<IActionResult> ChooseCompany()
    {
        if (User.GetUserId() is not int userId) return Challenge();
        return View(new ChooseCompanyViewModel { Assignments = await _assignments.ForUserAsync(userId) });
    }

    [HttpPost, AllowNoCompany, ValidateAntiForgeryToken]
    public async Task<IActionResult> ChooseCompany(int companyId)
    {
        if (User.GetUserId() is not int userId) return Challenge();
        if (await _access.CheckAsync(userId, companyId, Array.Empty<string>()) != CompanyAccessResult.Allowed)
            return StatusCode(StatusCodes.Status403Forbidden);

        await SignInWithCompanyAsync(userId, companyId);
        return RedirectToAction("Index", "Home");
    }

    [HttpPost, AllowNoCompany, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    private Task SignInAsync(AppUser user, int? companyId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.DisplayName)
        };
        if (companyId is not null)
            claims.Add(new Claim(ClaimsExtensions.CompanyIdClaim, companyId.Value.ToString()));

        return HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    }

    private Task SignInWithCompanyAsync(int userId, int companyId)
    {
        var claims = User.Claims
            .Where(c => c.Type != ClaimsExtensions.CompanyIdClaim)
            .Append(new Claim(ClaimsExtensions.CompanyIdClaim, companyId.ToString()));

        return HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    }
}
