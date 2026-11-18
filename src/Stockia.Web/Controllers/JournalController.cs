using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stockia.Web.Security;
using Stockia.Web.Services;

namespace Stockia.Web.Controllers;

// SR-2. Every action reads the company from the signed-in user (SR-33). Entering an entry is for the
// accounting writers; any assigned role may open a saved entry.
[Authorize]
public class JournalController : Controller
{
    private readonly JournalService _journal;
    public JournalController(JournalService journal) => _journal = journal;

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
            .Select(l => new NewJournalLine(l.AccountId, l.Debit ?? 0m, l.Credit ?? 0m))
            .ToList();

        var result = await _journal.PostAsync(companyId,
            new NewJournalEntry(form.Date, form.Reference, form.Description, lines));

        if (!result.Succeeded)
            return View(await Page(companyId, form, result.Errors));

        return RedirectToAction(nameof(Details), new { id = result.EntryId });
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id)
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();
        var entry = await _journal.GetEntryAsync(companyId, id);
        return entry is null ? NotFound() : View(entry);
    }

    private async Task<JournalNewViewModel> Page(int companyId, JournalEntryForm form, IEnumerable<string>? errors = null)
    {
        while (form.Lines.Count < 3) form.Lines.Add(new JournalLineForm());
        return new JournalNewViewModel
        {
            Form = form,
            Accounts = await _journal.ListPostableAccountsAsync(companyId),
            Errors = errors?.ToList() ?? new List<string>()
        };
    }
}
