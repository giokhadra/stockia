using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stockia.Web.Services;

namespace Stockia.Web.Controllers;

public class CompanyForm
{
    public string Name { get; set; } = "";
    public string? TaxNumber { get; set; }
    public string BaseCurrency { get; set; } = "USD";
    public string? Error { get; set; }
}

[Authorize]
public class CompanyController : Controller
{
    private readonly CompanyService _companies;
    public CompanyController(CompanyService companies) => _companies = companies;

    [HttpGet]
    public async Task<IActionResult> Index() => View(await _companies.ListAsync());

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
}