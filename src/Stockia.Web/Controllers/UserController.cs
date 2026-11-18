using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stockia.Web.Domain;
using Stockia.Web.Security;
using Stockia.Web.Services;

namespace Stockia.Web.Controllers;

public class UsersPageViewModel
{
    public int UserId { get; set; }
    public int CompanyId { get; set; }
    public int RoleId { get; set; }
    public string? Error { get; set; }
    public List<Assignment> Assignments { get; set; } = new();
    public List<AppUser> Users { get; set; } = new();
    public List<Company> Companies { get; set; } = new();
    public List<Role> Roles { get; set; } = new();
}

[Authorize]
[RequireCompanyRole(CompanyRoles.Administrator)]
public class UserController : Controller
{
    private readonly AssignmentService _assignments;
    public UserController(AssignmentService assignments) => _assignments = assignments;

    [HttpGet]
    public async Task<IActionResult> Index() => View(await Fill(new UsersPageViewModel()));

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Assign(UsersPageViewModel form)
    {
        var (assignment, error) = await _assignments.AssignAsync(form.UserId, form.CompanyId, form.RoleId);
        if (assignment is null)
        {
            ModelState.Clear();
            form.Error = error;
            return View("Index", await Fill(form));
        }
        return RedirectToAction(nameof(Index));
    }

    private async Task<UsersPageViewModel> Fill(UsersPageViewModel vm)
    {
        vm.Assignments = await _assignments.ListAsync();
        vm.Users = await _assignments.UsersAsync();
        vm.Companies = await _assignments.CompaniesAsync();
        vm.Roles = await _assignments.RolesAsync();
        return vm;
    }
}