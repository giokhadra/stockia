using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stockia.Web.Domain;
using Stockia.Web.Security;
using Stockia.Web.Services;

namespace Stockia.Web.Controllers;

public class WarehousesPageViewModel
{
    public int? Id { get; set; }
    public string Name { get; set; } = "";
    public string? Error { get; set; }
    public List<Warehouse> Warehouses { get; set; } = new();
}

/// <summary>SR-13 / UC-10: warehouses. Anyone assigned to the company may look; only the accounting roles write (SR-33).</summary>
[Authorize]
public class WarehousesController : Controller
{
    private readonly WarehouseService _warehouses;
    public WarehousesController(WarehouseService warehouses) => _warehouses = warehouses;

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();
        return View(new WarehousesPageViewModel { Warehouses = await _warehouses.ListAsync(companyId) });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequireCompanyRole(CompanyRoles.Administrator, CompanyRoles.ChiefAccountant, CompanyRoles.Accountant)]
    public async Task<IActionResult> Create(WarehousesPageViewModel form)
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();
        var (warehouse, error) = await _warehouses.CreateAsync(companyId, form.Name);
        if (warehouse is null)
        {
            ModelState.Clear();
            form.Error = error;
            form.Warehouses = await _warehouses.ListAsync(companyId);
            return View("Index", form);
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    [RequireCompanyRole(CompanyRoles.Administrator, CompanyRoles.ChiefAccountant, CompanyRoles.Accountant)]
    public async Task<IActionResult> Edit(int id)
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();
        var warehouse = await _warehouses.FindAsync(companyId, id);
        if (warehouse is null) return NotFound();
        return View(new WarehousesPageViewModel { Id = warehouse.Id, Name = warehouse.Name });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequireCompanyRole(CompanyRoles.Administrator, CompanyRoles.ChiefAccountant, CompanyRoles.Accountant)]
    public async Task<IActionResult> Edit(WarehousesPageViewModel form)
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();
        var (warehouse, error) = await _warehouses.UpdateAsync(companyId, form.Id ?? 0, form.Name);
        if (warehouse is null)
        {
            ModelState.Clear();
            form.Error = error;
            return View(form);
        }
        return RedirectToAction(nameof(Index));
    }
}