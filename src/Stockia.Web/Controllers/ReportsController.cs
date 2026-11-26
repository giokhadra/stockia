using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stockia.Web.Services;

namespace Stockia.Web.Controllers;

public class ReportsPageViewModel
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string? Error { get; set; }
    public FinancialStatements? Statements { get; set; }
}

/// <summary>SR-21 / UC-12: financial statements. Read-only, so every role assigned to the company may open the page
/// (SR-33). The company comes from the signed-in user, never from the address.</summary>
[Authorize]
public class ReportsController : Controller
{
    private readonly ReportService _reports;
    private readonly TimeProvider _clock;

    public ReportsController(ReportService reports, TimeProvider clock)
    {
        _reports = reports;
        _clock = clock;
    }

    // URL: /Reports?from=2026-01-01&to=2026-12-31. Without dates: from 1 January of this year to today.
    [HttpGet]
    public async Task<IActionResult> Index(DateTime? from, DateTime? to)
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();
        if (from is null && to is null)
        {
            var today = _clock.GetLocalNow().Date;
            from = new DateTime(today.Year, 1, 1);
            to = today;
        }

        var (statements, error) = await _reports.GenerateAsync(companyId, from, to);
        return View(new ReportsPageViewModel { From = from, To = to, Error = error, Statements = statements });
    }
}