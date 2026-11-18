using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;

namespace Stockia.Web.Services;

/// <summary>SR-13 / UC-10: the warehouses of one company. Every method takes the companyId of the signed-in user (SR-33).</summary>
public class WarehouseService
{
    private readonly StockiaDbContext _db;
    public WarehouseService(StockiaDbContext db) => _db = db;

    public Task<List<Warehouse>> ListAsync(int companyId) =>
        _db.Warehouses.Where(w => w.CompanyId == companyId).OrderBy(w => w.Name).ToListAsync();

    public Task<Warehouse?> FindAsync(int companyId, int id) =>
        _db.Warehouses.FirstOrDefaultAsync(w => w.CompanyId == companyId && w.Id == id);

    public async Task<(Warehouse? Warehouse, string? Error)> CreateAsync(int companyId, string? name)
    {
        var error = await CheckAsync(companyId, null, name);
        if (error is not null) return (null, error);

        var warehouse = new Warehouse { CompanyId = companyId, Name = name!.Trim() };
        _db.Warehouses.Add(warehouse);
        await _db.SaveChangesAsync();
        return (warehouse, null);
    }

    public async Task<(Warehouse? Warehouse, string? Error)> UpdateAsync(int companyId, int id, string? name)
    {
        var warehouse = await FindAsync(companyId, id);
        if (warehouse is null) return (null, "Warehouse not found.");
        var error = await CheckAsync(companyId, id, name);
        if (error is not null) return (null, error);

        warehouse.Name = name!.Trim();
        await _db.SaveChangesAsync();
        return (warehouse, null);
    }

    private async Task<string?> CheckAsync(int companyId, int? selfId, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "The warehouse name is required.";
        var clean = name.Trim();
        if (clean.Length > 100) return "The warehouse name can have at most 100 characters.";

        var lower = clean.ToLower();
        var self = selfId ?? 0;   // 0 never matches a real warehouse, so a new warehouse is compared with all of them
        if (await _db.Warehouses.AnyAsync(w => w.CompanyId == companyId && w.Id != self && w.Name.ToLower() == lower))
            return $"A warehouse named {clean} already exists in this company.";
        return null;
    }
}