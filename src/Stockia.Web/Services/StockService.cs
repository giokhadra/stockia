using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;

namespace Stockia.Web.Services;
public record StockShortage(int ItemId, string Item, decimal Wanted, decimal Available);
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
         /// <summary>Item 17 (SR-15): the stock items of a sale that the warehouse does not hold enough of. Two lines of the
    /// same item count together. An empty list means the sale is within stock.</summary>
    public async Task<List<StockShortage>> ShortagesAsync(int companyId, int warehouseId, IEnumerable<InvoiceLine> lines)
    {
        var result = new List<StockShortage>();
        foreach (var item in lines.Where(l => l.IsStock).GroupBy(l => l.ItemId))
        {
            var wanted = item.Sum(l => l.Quantity);
            var available = await QuantityAsync(companyId, item.Key, warehouseId);
            if (wanted > available)
                result.Add(new StockShortage(item.Key, item.First().Description, wanted, available));
        }
        return result;
    }

    /// <summary>Item 17 (SR-15 / UC-5 step 15): one "Out" movement per stock line of a posted sale. The quantity leaves
    /// at the weighted average cost of the warehouse (value / quantity); selling all of it takes all of its value, so
    /// no cent is left behind. A service line never moves stock. The movements are only added here; the caller saves.</summary>
    public async Task IssueSaleAsync(SalesInvoice invoice, int warehouseId)
    {
        var onHand = new Dictionary<int, (decimal Quantity, decimal Value)>();   // what is left, line after line
        foreach (var line in invoice.Lines.Where(l => l.IsStock))
        {
            if (!onHand.TryGetValue(line.ItemId, out var left))
                left = (await QuantityAsync(invoice.CompanyId, line.ItemId, warehouseId),
                        await ValueAsync(invoice.CompanyId, line.ItemId, warehouseId));

            var value = left.Quantity <= 0 ? 0m
                : line.Quantity >= left.Quantity ? left.Value
                : Money.Round(left.Value / left.Quantity * line.Quantity);

            _db.StockMovements.Add(new StockMovement
            {
                CompanyId = invoice.CompanyId,
                ItemId = line.ItemId,
                WarehouseId = warehouseId,
                InvoiceLineId = line.Id,
                Date = invoice.Date,
                Type = StockMovementTypes.Out,
                Quantity = -line.Quantity,
                Value = -value
            });
            onHand[line.ItemId] = (left.Quantity - line.Quantity, left.Value - value);
        }
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