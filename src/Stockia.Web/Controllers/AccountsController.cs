using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stockia.Web.Domain;
using Stockia.Web.Security;
using Stockia.Web.Services;

namespace Stockia.Web.Controllers;

public class AccountsPageViewModel
{
    public int? Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "Asset";
    public int? ParentId { get; set; }
    public string? Error { get; set; }
    public List<Account> Accounts { get; set; } = new();
}

[Authorize]
public class AccountsController : Controller
{
    private readonly AccountService _accounts;
    public AccountsController(AccountService accounts) => _accounts = accounts;

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();
        return View(new AccountsPageViewModel { Accounts = await _accounts.ListAsync(companyId) });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequireCompanyRole(CompanyRoles.Administrator, CompanyRoles.ChiefAccountant, CompanyRoles.Accountant)]
    public async Task<IActionResult> Create(AccountsPageViewModel form)
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();
        var (account, error) = await _accounts.CreateAsync(companyId, form.Code, form.Name, form.Type, form.ParentId);
        if (account is null)
        {
            ModelState.Clear();
            form.Error = error;
            form.Accounts = await _accounts.ListAsync(companyId);
            return View("Index", form);
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    [RequireCompanyRole(CompanyRoles.Administrator, CompanyRoles.ChiefAccountant, CompanyRoles.Accountant)]
    public async Task<IActionResult> Edit(int id)
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();
        var account = await _accounts.FindAsync(companyId, id);
        if (account is null) return NotFound();
        return View(new AccountsPageViewModel
        {
            Id = account.Id, Code = account.Code, Name = account.Name, Type = account.Type,
            ParentId = account.ParentId, Accounts = await _accounts.ListAsync(companyId)
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequireCompanyRole(CompanyRoles.Administrator, CompanyRoles.ChiefAccountant, CompanyRoles.Accountant)]
    public async Task<IActionResult> Edit(AccountsPageViewModel form)
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();
        var (account, error) = await _accounts.UpdateAsync(companyId, form.Id ?? 0, form.Name, form.Type, form.ParentId);
        if (account is null)
        {
            ModelState.Clear();
            form.Error = error;
            form.Accounts = await _accounts.ListAsync(companyId);
            return View(form);
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequireCompanyRole(CompanyRoles.Administrator, CompanyRoles.ChiefAccountant, CompanyRoles.Accountant)]
    public async Task<IActionResult> SetActive(int id, bool active)
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();
        await _accounts.SetActiveAsync(companyId, id, active);
        return RedirectToAction(nameof(Index));
    }
}