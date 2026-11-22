using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;

namespace Stockia.Web.Services;

/// <summary>SR-14 / UC-3: stock of one company. Every method takes the companyId of the signed-in user (SR-33).</summary>
public class StockService
{
    private readonly StockiaDbContext _db;
    public StockService(StockiaDbContext db) => _db = db;

    /// <summary>One "In" movement per stock line of a saved purchase invoice. A service line never moves stock.
    /// The value of a line is its net in the base currency: the same amount the purchase entry debits to the stock
    /// account (SR-4), so stock and ledger agree. The movements are only added here; the caller saves them.</summary>
    public void ReceivePurchase(PurchaseInvoice invoice, int warehouseId, decimal rate)
    {
        foreach (var line in invoice.Lines.Where(l => l.IsStock))
            _db.StockMovements.Add(new StockMovement
            {
                CompanyId = invoice.CompanyId,
                ItemId = line.ItemId,
                WarehouseId = warehouseId,
                InvoiceLineId = line.Id,
                Date = invoice.Date,
                Type = StockMovementTypes.In,
                Quantity = line.Quantity,
                Value = rate == 1m ? line.Net : ExchangeRate.Convert(line.Net, rate)
            });
    }

    /// <summary>Quantity of an item, in one warehouse or in all of them.</summary>
    public Task<decimal> QuantityAsync(int companyId, int itemId, int? warehouseId = null) =>
        Of(companyId, itemId, warehouseId).SumAsync(m => m.Quantity);

    /// <summary>Value of an item in the base currency, in one warehouse or in all of them.</summary>
    public Task<decimal> ValueAsync(int companyId, int itemId, int? warehouseId = null) =>
        Of(companyId, itemId, warehouseId).SumAsync(m => m.Value);

    /// <summary>Quantity of each item that has movements, all warehouses together (for the items page).</summary>
    public Task<Dictionary<int, decimal>> QuantitiesAsync(int companyId) =>
        _db.StockMovements.Where(m => m.CompanyId == companyId)
            .GroupBy(m => m.ItemId)
            .Select(g => new { ItemId = g.Key, Quantity = g.Sum(m => m.Quantity) })
            .ToDictionaryAsync(x => x.ItemId, x => x.Quantity);

    private IQueryable<StockMovement> Of(int companyId, int itemId, int? warehouseId) =>
        _db.StockMovements.Where(m => m.CompanyId == companyId && m.ItemId == itemId
                                      && (warehouseId == null || m.WarehouseId == warehouseId));
}