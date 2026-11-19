using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stockia.Web.Domain;
using Stockia.Web.Security;
using Stockia.Web.Services;

namespace Stockia.Web.Controllers;

public class ExchangeRateForm
{
    public string? Currency { get; set; }
    public DateTime Date { get; set; } = DateTime.Today;
    public decimal? Rate { get; set; }
}

public class ExchangeRatesViewModel
{
    public string BaseCurrency { get; set; } = "";
    public List<ExchangeRate> Rates { get; set; } = new();
    public ExchangeRateForm Form { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

/// <summary>SR-19 / UC-7: the exchange rates of the chosen company (SR-33). Anyone assigned may look;
/// only the accounting roles record a rate.</summary>
[Authorize]
public class ExchangeRatesController : Controller
{
    private readonly ExchangeRateService _rates;
    public ExchangeRatesController(ExchangeRateService rates) => _rates = rates;

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();
        return View(await Page(companyId, new ExchangeRateForm(), new List<string>()));
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequireCompanyRole(CompanyRoles.Administrator, CompanyRoles.ChiefAccountant, CompanyRoles.Accountant)]
    public async Task<IActionResult> Set(ExchangeRateForm form)
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();
        if (!ModelState.IsValid || form.Rate is null)
            return View("Index", await Page(companyId, form, new List<string> { "Check the date and the rate: one of them is not valid." }));

        var errors = await _rates.SetAsync(companyId, form.Currency, form.Date, form.Rate.Value);
        if (errors.Count > 0) return View("Index", await Page(companyId, form, errors));
        return RedirectToAction(nameof(Index));
    }

    private async Task<ExchangeRatesViewModel> Page(int companyId, ExchangeRateForm form, List<string> errors) => new()
    {
        BaseCurrency = await _rates.BaseCurrencyAsync(companyId),
        Rates = await _rates.ListAsync(companyId),
        Form = form,
        Errors = errors
    };
}
