using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stockia.Web.Domain;
using Stockia.Web.Security;
using Stockia.Web.Services;

namespace Stockia.Web.Controllers;

public class CompanyForm
{
    public string Name { get; set; } = "";
    public string? TaxNumber { get; set; }
    public string BaseCurrency { get; set; } = "USD";
    public string? Error { get; set; }
}

public record CompanyListViewModel(List<Company> Companies, Dictionary<int, int> PeriodCounts);

public record CompanyPeriodsViewModel(Company Company, List<FiscalPeriod> Periods);

[Authorize]
[RequireCompanyRole(CompanyRoles.Administrator)]
public class CompanyController : Controller
{
    private readonly CompanyService _companies;
    private readonly AssignmentService _assignments;

    public CompanyController(CompanyService companies, AssignmentService assignments)
    {
        _companies = companies;
        _assignments = assignments;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        if (User.GetUserId() is not int userId) return Challenge();
        return View(new CompanyListViewModel(
            await _companies.ListForUserAsync(userId),
            await _companies.PeriodCountsForUserAsync(userId)));
    }

    [HttpGet]
    public IActionResult Create() => View(new CompanyForm());

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CompanyForm form)
    {
        if (User.GetUserId() is not int userId) return Challenge();
        var (company, error) = await _companies.CreateAsync(
            form.Name, form.TaxNumber, form.BaseCurrency, userId);
        if (company is null)
        {
            ModelState.Clear();
            form.Error = error;
            return View(form);
        }
        return RedirectToAction(nameof(Index));
    }

    /// <summary>SR-18: the fiscal periods of one company the signed-in user is assigned to.</summary>
    [HttpGet]
    public async Task<IActionResult> Periods(int id)
    {
        if (User.GetUserId() is not int userId) return Challenge();
        var company = await _companies.FindAsync(id);
        if (company is null || !await _assignments.IsAssignedAsync(userId, id)) return NotFound();
        return View(new CompanyPeriodsViewModel(company, await _companies.PeriodsOfAsync(id)));
    }
}
