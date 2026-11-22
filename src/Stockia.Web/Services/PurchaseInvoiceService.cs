using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;

namespace Stockia.Web.Services;

/// <summary>What the user types for a supplier.</summary>
public record SupplierForm(string? Name, string? TaxNumber, string? Phone);

/// <summary>One typed line of a purchase invoice. A blank unit price takes the item's cost (BR-1: less typing).</summary>
public record PurchaseLineForm(int? ItemId, decimal? Quantity, decimal? UnitPrice);

/// <summary>What the user types for a purchase invoice. A blank currency means the company's base currency.</summary>
/// <summary>What the user types for a purchase invoice. A blank currency means the company's base currency.
/// WarehouseId (SR-14) receives the stock lines; left blank, the company's only warehouse is used.</summary>
public record PurchaseInvoiceForm(int? SupplierId, string? Number, DateTime? Date, DateTime? DueDate,
string? Currency, string? Description, IReadOnlyList<PurchaseLineForm> Lines, int? WarehouseId = null);

/// <summary>SR-7 / UC-3: suppliers and purchase invoices of one company. Every method takes the companyId of the
/// signed-in user (SR-33). Saving an invoice posts its journal entry (SR-4) and raises the supplier's balance.</summary>
public class PurchaseInvoiceService
{
    private readonly StockiaDbContext _db;
    private readonly PostingService _posting;
    private readonly IExchangeRates _rates;

    public PurchaseInvoiceService(StockiaDbContext db, PostingService posting, IExchangeRates rates)
    {
        _db = db;
        _posting = posting;
        _rates = rates;
    }

    // ---- suppliers ----

    public Task<List<Supplier>> ListSuppliersAsync(int companyId) =>
        _db.Suppliers.Where(s => s.CompanyId == companyId).OrderBy(s => s.Name).ToListAsync();

    public Task<Supplier?> FindSupplierAsync(int companyId, int id) =>
        _db.Suppliers.FirstOrDefaultAsync(s => s.CompanyId == companyId && s.Id == id);

    public async Task<(Supplier? Supplier, string? Error)> CreateSupplierAsync(int companyId, SupplierForm form)
    {
        var name = form.Name?.Trim();
        if (string.IsNullOrEmpty(name)) return (null, "The supplier name is required.");
        if (name.Length > 150) return (null, "The supplier name can have at most 150 characters.");
        var tax = Clean(form.TaxNumber);
        if (tax is { Length: > 30 }) return (null, "The tax number can have at most 30 characters.");
        var phone = Clean(form.Phone);
        if (phone is { Length: > 30 }) return (null, "The phone can have at most 30 characters.");

        var lower = name.ToLower();
        if (await _db.Partners.AnyAsync(p => p.CompanyId == companyId && p.Name.ToLower() == lower))
            return (null, $"{name} already exists in this company.");

        var supplier = new Supplier { CompanyId = companyId, Name = name, TaxNumber = tax, Phone = phone };
        _db.Suppliers.Add(supplier);
        await _db.SaveChangesAsync();
        return (supplier, null);
    }

    // ---- purchase invoices ----

    public Task<List<PurchaseInvoice>> ListAsync(int companyId) =>
        _db.PurchaseInvoices.Include(i => i.Partner)
            .Where(i => i.CompanyId == companyId)
            .OrderByDescending(i => i.Date).ThenByDescending(i => i.Id).ToListAsync();

    public Task<PurchaseInvoice?> FindAsync(int companyId, int id) =>
            _db.PurchaseInvoices.Include(i => i.Partner).Include(i => i.Lines).Include(i => i.Warehouse)
            .FirstOrDefaultAsync(i => i.CompanyId == companyId && i.Id == id);

    public Task<List<PurchaseInvoice>> ListOfSupplierAsync(int companyId, int supplierId) =>
        _db.PurchaseInvoices.Where(i => i.CompanyId == companyId && i.PartnerId == supplierId)
            .OrderByDescending(i => i.Date).ToListAsync();

    public async Task<(PurchaseInvoice? Invoice, IReadOnlyList<string> Errors)> CreateAsync(
        int companyId, int userId, PurchaseInvoiceForm form)
    {
        var errors = new List<string>();

        var supplier = form.SupplierId is int sid ? await FindSupplierAsync(companyId, sid) : null;
        if (supplier is null) errors.Add("Choose a supplier of this company.");

        var number = form.Number?.Trim();
        if (string.IsNullOrEmpty(number)) errors.Add("The invoice number is required.");
        else if (number.Length > 50) errors.Add("The invoice number can have at most 50 characters.");

        if (form.Date is null) errors.Add("The invoice date is required.");
        if (form.DueDate is null) errors.Add("The due date is required.");
        else if (form.Date is not null && form.DueDate.Value.Date < form.Date.Value.Date)
            errors.Add("The due date cannot be before the invoice date.");

        var baseCurrency = await _rates.BaseCurrencyAsync(companyId);
        var currency = string.IsNullOrWhiteSpace(form.Currency) ? baseCurrency : form.Currency.Trim().ToUpperInvariant();
        if (!ExchangeRate.IsCurrencyCode(currency)) errors.Add("The currency must be 3 letters, such as USD.");

        var description = Clean(form.Description);
        if (description is { Length: > 500 }) errors.Add("The description can have at most 500 characters.");

        var typed = form.Lines.Where(l => l.ItemId is not null || l.Quantity is not null || l.UnitPrice is not null).ToList();
        if (typed.Count == 0) errors.Add("Add at least one line.");

        var itemIds = typed.Where(l => l.ItemId is not null).Select(l => l.ItemId!.Value).Distinct().ToList();
        var items = await _db.Items.Where(i => i.CompanyId == companyId && itemIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id);

        var invoice = new PurchaseInvoice
        {
            CompanyId = companyId,
            PartnerId = supplier?.Id ?? 0,
            Number = number ?? "",
            Date = form.Date?.Date ?? default,
            DueDate = form.DueDate?.Date ?? default,
            Currency = currency,
            Description = description
        };

        for (var n = 0; n < typed.Count; n++)
        {
            var l = typed[n];
            var row = $"Line {n + 1}: ";
            if (l.ItemId is not int itemId || !items.TryGetValue(itemId, out var item))
            {
                errors.Add(row + "choose an item of this company.");
                continue;
            }
            if (l.Quantity is null or <= 0) { errors.Add(row + "the quantity must be more than 0."); continue; }
            var price = l.UnitPrice ?? item.Cost;
            if (price < 0) { errors.Add(row + "the unit price cannot be negative."); continue; }

            invoice.Lines.Add(new InvoiceLine
            {
                CompanyId = companyId,
                ItemId = item.Id,
                Description = item.Name,
                Quantity = Money.Round(l.Quantity.Value),
                UnitPrice = Money.Round(price),
                VatRate = item.VatRate,          // the rate of today stays on the line (BRD OI-8)
                IsStock = item.IsStock
            });
        }
              
        if (invoice.Lines.Any(l => l.IsStock))
        {
            var warehouseIds = await _db.Warehouses.Where(w => w.CompanyId == companyId).Select(w => w.Id).ToListAsync();
            if (form.WarehouseId is int chosen)
            {
                if (warehouseIds.Contains(chosen)) invoice.WarehouseId = chosen;
                else errors.Add("Choose a warehouse of this company.");
            }
            else if (warehouseIds.Count == 1) invoice.WarehouseId = warehouseIds[0];
            else errors.Add(warehouseIds.Count == 0
                ? "Add a warehouse first: stock items are received into a warehouse."
                : "Choose the warehouse that receives the stock items.");
        }
        if (errors.Count > 0) return (null, errors);

        invoice.Recalculate();
        // the balance of the supplier is kept in the base currency (SR-19)
        decimal rate = 1m;
        if (currency != baseCurrency)
        {
            var found = await _rates.RateOnAsync(companyId, currency, invoice.Date);
            if (found is null)
                return (null, new[] { $"There is no exchange rate for {currency} on or before {invoice.Date:yyyy-MM-dd}. Add it first." });
            rate = found.Rate;
        }
        var postable = ToPostable(invoice, rate);
        invoice.BaseTotal = BaseTotalOf(postable);

        if (await _db.PurchaseInvoices.AnyAsync(i => i.CompanyId == companyId && i.PartnerId == invoice.PartnerId && i.Number == invoice.Number))
            return (null, new[] { $"Purchase invoice {invoice.Number} of {supplier!.Name} already exists." });

        _db.PurchaseInvoices.Add(invoice);
        await _db.SaveChangesAsync();           // the invoice needs its Id to be posted

        var posted = await _posting.PostAsync(companyId, userId, postable with { Id = invoice.Id });
        if (!posted.Succeeded)
        {
            _db.PurchaseInvoices.Remove(invoice);   // nothing is kept of an invoice that could not be posted
            await _db.SaveChangesAsync();
            return (null, posted.Errors);
        }

               supplier!.Balance += invoice.BaseTotal;     // SR-7: saving the invoice raises what we owe the supplier
        if (invoice.WarehouseId is int warehouseId)  // SR-14: the stock rises in the same save
            new StockService(_db).ReceivePurchase(invoice, warehouseId, rate);
        await _db.SaveChangesAsync();
        return (invoice, Array.Empty<string>());
    }

    /// <summary>The invoice as the posting service wants it, in the base currency (the rate is 1 for the base currency).</summary>
    private static PostableDocument ToPostable(PurchaseInvoice i, decimal rate) =>
        new(DocumentTypes.PurchaseInvoice, i.Id, i.Number, i.Date, i.Description,
            i.Lines.Select(l => new DocumentLine(l.Description,
                rate == 1m ? l.Net : ExchangeRate.Convert(l.Net, rate), l.VatRate, l.IsStock)).ToList());

    /// <summary>Net plus VAT, worked out exactly as the posting rule does, so balance and ledger agree.</summary>
    private static decimal BaseTotalOf(PostableDocument d) => InvoiceMath.Net(d.Lines) + InvoiceMath.Vat(d.Lines);

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
