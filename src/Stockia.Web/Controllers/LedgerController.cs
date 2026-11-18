using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stockia.Web.Services;

namespace Stockia.Web.Controllers;

// SR-2. Read-only pages: every assigned role of the chosen company may open them.
[Authorize]
public class LedgerController : Controller
{
    private readonly JournalService _journal;
    public LedgerController(JournalService journal) => _journal = journal;

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();
        return View(await _journal.ListAccountsAsync(companyId));
    }

    // URL: /Ledger/Account/{id}. An account of another company gives 404, same as a missing one.
    [HttpGet, ActionName("Account")]
    public async Task<IActionResult> ShowAccount(int id)
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();
        var ledger = await _journal.GetLedgerAsync(companyId, id);
        return ledger is null ? NotFound() : View("Account", ledger);
    }
}
