using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stockia.Web.Domain;
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
public class CompanyController : Controller
{
    private readonly CompanyService _companies;
    public CompanyController(CompanyService companies) => _companies = companies;

    [HttpGet]
    public async Task<IActionResult> Index() =>
        View(new CompanyListViewModel(await _companies.ListAsync(), await _companies.PeriodCountsAsync()));

    [HttpGet]
    public IActionResult Create() => View(new CompanyForm());

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CompanyForm form)
    {
        var (company, error) = await _companies.CreateAsync(form.Name, form.TaxNumber, form.BaseCurrency);
        if (company is null)
        {
            ModelState.Clear();
            form.Error = error;
            return View(form);
        }
        return RedirectToAction(nameof(Index));
    }

    /// <summary>SR-18: the fiscal periods of one company.</summary>
    [HttpGet]
    public async Task<IActionResult> Periods(int id)
    {
        var company = await _companies.FindAsync(id);
        if (company is null) return NotFound();
        return View(new CompanyPeriodsViewModel(company, await _companies.PeriodsOfAsync(id)));
    }
}
