using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stockia.Web.Domain;
using Stockia.Web.Security;
using Stockia.Web.Services;

namespace Stockia.Web.Controllers;

public class ItemsPageViewModel
{
    public int? Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";
    public string? Category { get; set; }
    public decimal? Cost { get; set; }
    public decimal? SalePrice { get; set; }
    public decimal? VatRate { get; set; } = 11m;
    public bool IsStock { get; set; } = true;
    public string? Error { get; set; }
    public List<Item> Items { get; set; } = new();
}

/// <summary>SR-13 / UC-10: items. Anyone assigned to the company may look; only the accounting roles write (SR-33).</summary>
[Authorize]
public class ItemsController : Controller
{
    private readonly ItemService _items;
    public ItemsController(ItemService items) => _items = items;

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();
        return View(new ItemsPageViewModel { Items = await _items.ListAsync(companyId) });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequireCompanyRole(CompanyRoles.Administrator, CompanyRoles.ChiefAccountant, CompanyRoles.Accountant)]
    public async Task<IActionResult> Create(ItemsPageViewModel form)
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();
        var (item, error) = await _items.CreateAsync(companyId, ToForm(form));
        if (item is null)
        {
            ModelState.Clear();
            form.Error = error;
            form.Items = await _items.ListAsync(companyId);
            return View("Index", form);
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    [RequireCompanyRole(CompanyRoles.Administrator, CompanyRoles.ChiefAccountant, CompanyRoles.Accountant)]
    public async Task<IActionResult> Edit(int id)
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();
        var item = await _items.FindAsync(companyId, id);
        if (item is null) return NotFound();
        return View(new ItemsPageViewModel
        {
            Id = item.Id, Code = item.Code, Name = item.Name, Unit = item.Unit, Category = item.Category,
            Cost = item.Cost, SalePrice = item.SalePrice, VatRate = item.VatRate, IsStock = item.IsStock
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequireCompanyRole(CompanyRoles.Administrator, CompanyRoles.ChiefAccountant, CompanyRoles.Accountant)]
    public async Task<IActionResult> Edit(ItemsPageViewModel form)
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();
        var (item, error) = await _items.UpdateAsync(companyId, form.Id ?? 0, ToForm(form));
        if (item is null)
        {
            ModelState.Clear();
            form.Error = error;
            // the code is not posted back (it cannot be changed): read it again so the page can show it
            form.Code = (await _items.FindAsync(companyId, form.Id ?? 0))?.Code ?? form.Code;
            return View(form);
        }
        return RedirectToAction(nameof(Index));
    }

    private static ItemForm ToForm(ItemsPageViewModel f) =>
        new(f.Code, f.Name, f.Unit, f.Category, f.Cost, f.SalePrice, f.VatRate, f.IsStock);
}