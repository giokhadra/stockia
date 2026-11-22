using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stockia.Web.Domain;
using Stockia.Web.Security;
using Stockia.Web.Services;

namespace Stockia.Web.Controllers;

public class SuppliersPageViewModel
{
    public string Name { get; set; } = "";
    public string? TaxNumber { get; set; }
    public string? Phone { get; set; }
    public string? Error { get; set; }
    public List<Supplier> Suppliers { get; set; } = new();
}

/// <summary>SR-7 / UC-3: suppliers and their balance. Anyone assigned to the company may look; only the accounting roles write (SR-33).</summary>
[Authorize]
public class SuppliersController : Controller
{
    private readonly PurchaseInvoiceService _purchases;
    public SuppliersController(PurchaseInvoiceService purchases) => _purchases = purchases;

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();
        return View(new SuppliersPageViewModel { Suppliers = await _purchases.ListSuppliersAsync(companyId) });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequireCompanyRole(CompanyRoles.Administrator, CompanyRoles.ChiefAccountant, CompanyRoles.Accountant)]
    public async Task<IActionResult> Create(SuppliersPageViewModel form)
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();
        var (supplier, error) = await _purchases.CreateSupplierAsync(companyId,
            new SupplierForm(form.Name, form.TaxNumber, form.Phone));
        if (supplier is null)
        {
            ModelState.Clear();
            form.Error = error;
            form.Suppliers = await _purchases.ListSuppliersAsync(companyId);
            return View("Index", form);
        }
        return RedirectToAction(nameof(Index));
    }
}
