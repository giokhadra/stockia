using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stockia.Web.Security;
using Stockia.Web.Services;

namespace Stockia.Web.Controllers;

// SR-2 and SR-6. Every action reads the company from the signed-in user (SR-33). Entering and reversing an entry
// are for the accounting writers; any assigned role may open a saved entry and read its history.
// A posted entry is never deleted: there is no Delete action, only Reverse.
[Authorize]
public class JournalController : Controller
{
    private readonly JournalService _journal;
    private readonly AuditService _audit;
    private readonly JournalReversalService _reversals;
    private readonly ExchangeRateService _rates;

    public JournalController(JournalService journal, AuditService audit, JournalReversalService reversals,
        ExchangeRateService rates)
    {
        _journal = journal;
        _audit = audit;
        _reversals = reversals;
        _rates = rates;
    }

    [HttpGet]
    [RequireCompanyRole(CompanyRoles.Administrator, CompanyRoles.ChiefAccountant, CompanyRoles.Accountant)]
    public async Task<IActionResult> New()
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();
        return View(await Page(companyId, new JournalEntryForm()));
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequireCompanyRole(CompanyRoles.Administrator, CompanyRoles.ChiefAccountant, CompanyRoles.Accountant)]
    public async Task<IActionResult> New(JournalEntryForm form)
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();

        if (!ModelState.IsValid)
            return View(await Page(companyId, form, new[] { "Check the date and the amounts: one of them is not valid." }));

        // Rows left completely blank on the page are ignored.
        var lines = form.Lines
            .Where(l => !(l.AccountId == 0 && (l.Debit ?? 0m) == 0m && (l.Credit ?? 0m) == 0m))
            .Select(l => new NewJournalLine(l.AccountId, l.Debit ?? 0m, l.Credit ?? 0m, l.Currency))
            .ToList();

        var result = await _journal.PostAsync(companyId,
            new NewJournalEntry(form.Date, form.Reference, form.Description, lines));

        if (!result.Succeeded)
            return View(await Page(companyId, form, result.Errors));

        // SR-6: who posted it, and when
        if (User.GetUserId() is int userId)
            await _audit.RecordAsync(companyId, userId, AuditActions.EntryPosted, form.Reference?.Trim(), result.EntryId);

        return RedirectToAction(nameof(Details), new { id = result.EntryId });
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id)
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();
        return await DetailsPage(companyId, id, null);
    }

    /// <summary>SR-6: corrects a posted entry by a reversal (a new entry with debits and credits swapped).</summary>
    [HttpPost, ValidateAntiForgeryToken]
    [RequireCompanyRole(CompanyRoles.Administrator, CompanyRoles.ChiefAccountant, CompanyRoles.Accountant)]
    public async Task<IActionResult> Reverse(int id)
    {
        if (User.GetCompanyId() is not int companyId || User.GetUserId() is not int userId) return Forbid();

        var result = await _reversals.ReverseAsync(companyId, userId, id, DateTime.Today);
        if (result.Succeeded)
            return RedirectToAction(nameof(Details), new { id = result.EntryId });

        // an entry of another company is NotFound; an entry already reversed shows the reason on its own page
        return await DetailsPage(companyId, id, result.Error);
    }

    private async Task<IActionResult> DetailsPage(int companyId, int id, string? reverseError)
    {
        var entry = await _journal.GetEntryAsync(companyId, id);
        if (entry is null) return NotFound();

        ViewData["History"] = await _audit.ForEntryAsync(companyId, id);
        ViewData["ReversedById"] = await _reversals.FindReversalIdAsync(companyId, id);
        ViewData["ReverseError"] = reverseError;
        ViewData["BaseCurrency"] = await _rates.BaseCurrencyAsync(companyId);
        return View("Details", entry);
    }

    private async Task<JournalNewViewModel> Page(int companyId, JournalEntryForm form, IEnumerable<string>? errors = null)
    {
        while (form.Lines.Count < 3) form.Lines.Add(new JournalLineForm());
        return new JournalNewViewModel
        {
            Form = form,
            Accounts = await _journal.ListPostableAccountsAsync(companyId),
            BaseCurrency = await _rates.BaseCurrencyAsync(companyId),
            Currencies = await _rates.CurrenciesAsync(companyId),
            Errors = errors?.ToList() ?? new List<string>()
        };
    }
}