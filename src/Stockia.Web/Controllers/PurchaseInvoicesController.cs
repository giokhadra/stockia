using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Stockia.Web.Domain;
using Stockia.Web.Security;
using Stockia.Web.Services;

namespace Stockia.Web.Controllers;

public class PurchaseLineInput
{
    public int? ItemId { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
}

public class PurchaseInvoiceViewModel
{
    public const int LineRows = 5;
    public int? SupplierId { get; set; }
    public string Number { get; set; } = "";
    public DateTime? Date { get; set; }
    public DateTime? DueDate { get; set; }
    public string? Currency { get; set; }
    public string? Description { get; set; }
    public int? WarehouseId { get; set; }
    public List<PurchaseLineInput> Lines { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public List<Supplier> Suppliers { get; set; } = new();
    public List<Item> Items { get; set; } = new();
     public List<Warehouse> Warehouses { get; set; } = new();

     public IEnumerable<SelectListItem> WarehouseChoices =>
     Warehouses.Select(w => new SelectListItem(w.Name, w.Id.ToString(), w.Id == WarehouseId));
    public IEnumerable<SelectListItem> SupplierChoices =>
        Suppliers.Select(s => new SelectListItem(s.Name, s.Id.ToString(), s.Id == SupplierId));
    public IEnumerable<SelectListItem> ItemChoices(int? selected) =>
        Items.Select(i => new SelectListItem($"{i.Code} - {i.Name}", i.Id.ToString(), i.Id == selected));
}

/// <summary>SR-7 / UC-3: purchase invoices. Anyone assigned to the company may look; only the accounting roles write (SR-33).</summary>
[Authorize]
public class PurchaseInvoicesController : Controller
{
    private readonly PurchaseInvoiceService _purchases;
    private readonly ItemService _items;
    private readonly WarehouseService _warehouses;
    private readonly TimeProvider _clock;

    public PurchaseInvoicesController(PurchaseInvoiceService purchases, ItemService items, WarehouseService warehouses,
        TimeProvider clock)
    {
        _purchases = purchases;
        _items = items;
        _warehouses = warehouses;
        _clock = clock;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();
        return View(await _purchases.ListAsync(companyId));
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id)
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();
        var invoice = await _purchases.FindAsync(companyId, id);
        return invoice is null ? NotFound() : View(invoice);
    }

    [HttpGet]
    [RequireCompanyRole(CompanyRoles.Administrator, CompanyRoles.ChiefAccountant, CompanyRoles.Accountant)]
    public async Task<IActionResult> New()
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();
        var today = _clock.GetUtcNow().UtcDateTime.Date;
        return View(await Fill(companyId, new PurchaseInvoiceViewModel { Date = today, DueDate = today.AddDays(30) }));
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequireCompanyRole(CompanyRoles.Administrator, CompanyRoles.ChiefAccountant, CompanyRoles.Accountant)]
    public async Task<IActionResult> New(PurchaseInvoiceViewModel form)
    {
        if (User.GetCompanyId() is not int companyId || User.GetUserId() is not int userId) return Forbid();
        var lines = (form.Lines ?? new()).Select(l => new PurchaseLineForm(l.ItemId, l.Quantity, l.UnitPrice)).ToList();
        var (invoice, errors) = await _purchases.CreateAsync(companyId, userId,
            new PurchaseInvoiceForm(form.SupplierId, form.Number, form.Date, form.DueDate, form.Currency, form.Description, lines,
                form.WarehouseId));
        if (invoice is null)
        {
            ModelState.Clear();
            form.Errors = errors.ToList();
            return View(await Fill(companyId, form));
        }
        return RedirectToAction(nameof(Details), new { id = invoice.Id });
    }

    private async Task<PurchaseInvoiceViewModel> Fill(int companyId, PurchaseInvoiceViewModel m)
    {
        m.Suppliers = await _purchases.ListSuppliersAsync(companyId);
        m.Items = await _items.ListAsync(companyId);
                m.Warehouses = await _warehouses.ListAsync(companyId);
        if (m.WarehouseId is null && m.Warehouses.Count == 1) m.WarehouseId = m.Warehouses[0].Id;   // SR-14: the only one
        while (m.Lines.Count < PurchaseInvoiceViewModel.LineRows) m.Lines.Add(new PurchaseLineInput());
        return m;
    }
}
