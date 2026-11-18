using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;

namespace Stockia.Web.Services;

/// <summary>What the user types for an item. Amounts are nullable so that a blank box is reported, not read as 0.</summary>
public record ItemForm(string? Code, string? Name, string? Unit, string? Category,
    decimal? Cost, decimal? SalePrice, decimal? VatRate, bool IsStock);

/// <summary>SR-13 / UC-10: the items of one company. Every method takes the companyId of the signed-in user (SR-33).</summary>
public class ItemService
{
    private readonly StockiaDbContext _db;
    public ItemService(StockiaDbContext db) => _db = db;

    public Task<List<Item>> ListAsync(int companyId) =>
        _db.Items.Where(i => i.CompanyId == companyId).OrderBy(i => i.Code).ToListAsync();

    public Task<Item?> FindAsync(int companyId, int id) =>
        _db.Items.FirstOrDefaultAsync(i => i.CompanyId == companyId && i.Id == id);

    public async Task<(Item? Item, string? Error)> CreateAsync(int companyId, ItemForm form)
    {
        var code = form.Code?.Trim();
        if (string.IsNullOrEmpty(code)) return (null, "The item code is required.");
        if (code.Length > 20) return (null, "The item code can have at most 20 characters.");

        // BRD SR-13: a duplicate item code is rejected (compared without regard to upper/lower case).
        var lower = code.ToLower();
        if (await _db.Items.AnyAsync(i => i.CompanyId == companyId && i.Code.ToLower() == lower))
            return (null, $"The item code {code} already exists in this company.");

        var error = Check(form);
        if (error is not null) return (null, error);

        var item = new Item { CompanyId = companyId, Code = code };
        Apply(item, form);
        _db.Items.Add(item);
        await _db.SaveChangesAsync();
        return (item, null);
    }

    /// <summary>The code never changes (invoices will refer to it); everything else can be edited,
    /// including the VAT rate, because the law can change it (BRD OI-8).</summary>
    public async Task<(Item? Item, string? Error)> UpdateAsync(int companyId, int id, ItemForm form)
    {
        var item = await FindAsync(companyId, id);
        if (item is null) return (null, "Item not found.");
        var error = Check(form);
        if (error is not null) return (null, error);

        Apply(item, form);
        await _db.SaveChangesAsync();
        return (item, null);
    }

    private static string? Check(ItemForm f)
    {
        if (string.IsNullOrWhiteSpace(f.Name)) return "The item name is required.";
        if (f.Name.Trim().Length > 100) return "The item name can have at most 100 characters.";
        if (string.IsNullOrWhiteSpace(f.Unit)) return "The unit is required.";
        if (f.Unit.Trim().Length > 10) return "The unit can have at most 10 characters.";
        if (f.Category is not null && f.Category.Trim().Length > 50) return "The category can have at most 50 characters.";
        if (f.Cost < 0) return "The cost cannot be negative.";
        if (f.SalePrice is null) return "The sale price is required.";
        if (f.SalePrice < 0) return "The sale price cannot be negative.";
        if (f.VatRate is null) return "The VAT rate is required.";
        if (f.VatRate < 0 || f.VatRate > 100) return "The VAT rate must be between 0 and 100.";
        return null;
    }

    private static void Apply(Item item, ItemForm f)
    {
        item.Name = f.Name!.Trim();
        item.Unit = f.Unit!.Trim();
        item.Category = string.IsNullOrWhiteSpace(f.Category) ? null : f.Category.Trim();
        item.Cost = Money.Round(f.Cost ?? 0m);              // SR-39: DECIMAL(18,2)
        item.SalePrice = Money.Round(f.SalePrice!.Value);
        item.VatRate = Money.Round(f.VatRate!.Value);
        item.IsStock = f.IsStock;
    }
}