using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stockia.Web.Domain;
using Stockia.Web.Security;
using Stockia.Web.Services;

namespace Stockia.Web.Controllers;

public class ReceiptAllocationInput
{
    public int InvoiceId { get; set; }
    public decimal? Amount { get; set; }
}

public class ReceiptViewModel
{
    public int? CustomerId { get; set; }
    public string? Number { get; set; }
    public DateTime? Date { get; set; }
    public string? Method { get; set; } = ReceiptMethods.Bank;
    public string? Description { get; set; }
    public List<ReceiptAllocationInput> Allocations { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public List<Customer> Customers { get; set; } = new();
    public Customer? Customer { get; set; }
    public List<OpenInvoice> Open { get; set; } = new();

    /// <summary>The amount typed for an open invoice, kept when the page comes back with errors.</summary>
    public decimal? TypedFor(int invoiceId) => Allocations.FirstOrDefault(a => a.InvoiceId == invoiceId)?.Amount;
}

/// <summary>SR-11 / UC-6: customer receipts of the chosen company (SR-33). Anyone assigned may look; only the
/// accounting roles record a receipt.</summary>
[Authorize]
public class ReceiptsController : Controller
{
    private readonly ReceiptService _receipts;
    private readonly SalesInvoiceService _sales;
    private readonly TimeProvider _clock;

    public ReceiptsController(ReceiptService receipts, SalesInvoiceService sales, TimeProvider clock)
    {
        _receipts = receipts;
        _sales = sales;
        _clock = clock;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();
        return View(await _receipts.ListAsync(companyId));
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id)
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();
        var receipt = await _receipts.FindAsync(companyId, id);
        return receipt is null ? NotFound() : View(receipt);
    }

    [HttpGet]
    [RequireCompanyRole(CompanyRoles.Administrator, CompanyRoles.ChiefAccountant, CompanyRoles.Accountant)]
    public async Task<IActionResult> New(int? customerId)
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();
        return View(await Fill(companyId, new ReceiptViewModel
        {
            CustomerId = customerId,
            Date = _clock.GetUtcNow().UtcDateTime.Date
        }));
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequireCompanyRole(CompanyRoles.Administrator, CompanyRoles.ChiefAccountant, CompanyRoles.Accountant)]
    public async Task<IActionResult> New(ReceiptViewModel form)
    {
        if (User.GetCompanyId() is not int companyId || User.GetUserId() is not int userId) return Forbid();
        var (receipt, errors) = await _receipts.CreateAsync(companyId, userId, new ReceiptForm(
            form.CustomerId, form.Number, form.Date, form.Method, form.Description,
            (form.Allocations ?? new()).Select(a => new ReceiptAllocationForm(a.InvoiceId, a.Amount)).ToList()));
        if (receipt is null)
        {
            ModelState.Clear();
            form.Errors = errors.ToList();
            return View(await Fill(companyId, form));
        }
        return RedirectToAction(nameof(Details), new { id = receipt.Id });
    }

    private async Task<ReceiptViewModel> Fill(int companyId, ReceiptViewModel m)
    {
        m.Customers = await _sales.ListCustomersAsync(companyId);
        m.Customer = m.Customers.FirstOrDefault(c => c.Id == m.CustomerId);
        m.Open = m.Customer is null ? new() : await _receipts.OpenInvoicesAsync(companyId, m.Customer.Id);
        return m;
    }
}
