using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;

namespace Stockia.Web.Services;

/// <summary>What the user types for a customer.</summary>
public record CustomerForm(string? Name, string? TaxNumber, string? Phone);

/// <summary>One typed line of a sales invoice. A blank unit price takes the item's sale price (BR-1: less typing).</summary>
public record SalesLineForm(int? ItemId, decimal? Quantity, decimal? UnitPrice);

/// <summary>What the user types for a sales invoice. A blank number takes the company's next number (S-0001...);
/// a blank currency means the base currency; WarehouseId is where the stock lines leave from (left blank, the
/// company's only warehouse).</summary>
public record SalesInvoiceForm(int? CustomerId, string? Number, DateTime? Date, DateTime? DueDate,
    string? Currency, string? Description, IReadOnlyList<SalesLineForm> Lines, int? WarehouseId = null);

/// <summary>SR-10 / UC-5: customers and sales invoices of one company. Every method takes the companyId of the
/// signed-in user (SR-33). Saving an invoice posts its journal entry (SR-4: Dr receivable, Cr sales, Cr VAT
/// collected) and raises the customer's balance, in one go: an invoice that cannot be posted is not kept.</summary>
public class SalesInvoiceService
{
    public const string NumberPrefix = "S-";

    private readonly StockiaDbContext _db;
    private readonly PostingService _posting;
    private readonly IExchangeRates _rates;

    public SalesInvoiceService(StockiaDbContext db, PostingService posting, IExchangeRates rates)
    {
        _db = db;
        _posting = posting;
        _rates = rates;
    }

    // ---- customers ----

    public Task<List<Customer>> ListCustomersAsync(int companyId) =>
        _db.Customers.Where(c => c.CompanyId == companyId).OrderBy(c => c.Name).ToListAsync();

    public Task<Customer?> FindCustomerAsync(int companyId, int id) =>
        _db.Customers.FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == id);

    public async Task<(Customer? Customer, string? Error)> CreateCustomerAsync(int companyId, CustomerForm form)
    {
        var name = form.Name?.Trim();
        if (string.IsNullOrEmpty(name)) return (null, "The customer name is required.");
        if (name.Length > 150) return (null, "The customer name can have at most 150 characters.");
        var tax = Clean(form.TaxNumber);
        if (tax is { Length: > 30 }) return (null, "The tax number can have at most 30 characters.");
        var phone = Clean(form.Phone);
        if (phone is { Length: > 30 }) return (null, "The phone can have at most 30 characters.");

        // one name per company across suppliers and customers (Partner table, db/011)
        var lower = name.ToLower();
        if (await _db.Partners.AnyAsync(p => p.CompanyId == companyId && p.Name.ToLower() == lower))
            return (null, $"{name} already exists in this company.");

        var customer = new Customer { CompanyId = companyId, Name = name, TaxNumber = tax, Phone = phone };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();
        return (customer, null);
    }

    // ---- sales invoices ----

    public Task<List<SalesInvoice>> ListAsync(int companyId) =>
        _db.SalesInvoices.Include(i => i.Partner)
            .Where(i => i.CompanyId == companyId)
            .OrderByDescending(i => i.Date).ThenByDescending(i => i.Id).ToListAsync();

    public Task<SalesInvoice?> FindAsync(int companyId, int id) =>
        _db.SalesInvoices.Include(i => i.Partner).Include(i => i.Lines).Include(i => i.Warehouse)
            .FirstOrDefaultAsync(i => i.CompanyId == companyId && i.Id == id);

    public Task<List<SalesInvoice>> ListOfCustomerAsync(int companyId, int customerId) =>
        _db.SalesInvoices.Where(i => i.CompanyId == companyId && i.PartnerId == customerId)
            .OrderByDescending(i => i.Date).ToListAsync();

    /// <summary>The next number of the company: S-0001, S-0002... (numbers typed in another form are skipped).</summary>
    public async Task<string> NextNumberAsync(int companyId)
    {
        var numbers = await _db.SalesInvoices.Where(i => i.CompanyId == companyId && i.Number.StartsWith(NumberPrefix))
            .Select(i => i.Number).ToListAsync();
        var last = numbers.Select(n => int.TryParse(n[NumberPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0)
            .DefaultIfEmpty(0).Max();
        return NumberPrefix + (last + 1).ToString("0000", CultureInfo.InvariantCulture);
    }

    public async Task<(SalesInvoice? Invoice, IReadOnlyList<string> Errors)> CreateAsync(
        int companyId, int userId, SalesInvoiceForm form)
    {
        var errors = new List<string>();

        var customer = form.CustomerId is int cid ? await FindCustomerAsync(companyId, cid) : null;
        if (customer is null) errors.Add("Choose a customer of this company.");

        var number = string.IsNullOrWhiteSpace(form.Number) ? await NextNumberAsync(companyId) : form.Number.Trim();
        if (number.Length > 50) errors.Add("The invoice number can have at most 50 characters.");

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

        var invoice = new SalesInvoice
        {
            CompanyId = companyId,
            PartnerId = customer?.Id ?? 0,
            Number = number,
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
            var price = l.UnitPrice ?? item.SalePrice;
            if (price < 0) { errors.Add(row + "the unit price cannot be negative."); continue; }

            invoice.Lines.Add(new InvoiceLine
            {
                CompanyId = companyId,
                ItemId = item.Id,
                Description = item.Name,
                Quantity = Money.Round(l.Quantity.Value),
                UnitPrice = Money.Round(price),
                VatRate = item.VatRate,          // today's rate stays on the line (BRD OI-8)
                IsStock = item.IsStock           // a service line never moves stock
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
                ? "Add a warehouse first: stock items leave from a warehouse."
                : "Choose the warehouse the stock items leave from.");
        }
        if (errors.Count > 0) return (null, errors);

        invoice.Recalculate();
        decimal rate = 1m;   // the customer's balance is kept in the base currency (SR-19)
        if (currency != baseCurrency)
        {
            var found = await _rates.RateOnAsync(companyId, currency, invoice.Date);
            if (found is null)
                return (null, new[] { $"There is no exchange rate for {currency} on or before {invoice.Date:yyyy-MM-dd}. Add it first." });
            rate = found.Rate;
        }
        var postable = ToPostable(invoice, rate);
        invoice.BaseTotal = InvoiceMath.Net(postable.Lines) + InvoiceMath.Vat(postable.Lines);

        if (await _db.SalesInvoices.AnyAsync(i => i.CompanyId == companyId && i.Number == invoice.Number))
            return (null, new[] { $"Sales invoice {invoice.Number} already exists in this company." });

        _db.SalesInvoices.Add(invoice);
        await _db.SaveChangesAsync();           // the invoice needs its Id to be posted

        var posted = await _posting.PostAsync(companyId, userId, postable with { Id = invoice.Id });
        if (!posted.Succeeded)
        {
            _db.SalesInvoices.Remove(invoice);   // nothing is kept of an invoice that could not be posted
            await _db.SaveChangesAsync();
            return (null, posted.Errors);
        }

        customer!.Balance += invoice.BaseTotal;  // SR-10: what the customer owes us rises by the invoice
        await _db.SaveChangesAsync();
        return (invoice, Array.Empty<string>());
    }

    /// <summary>The invoice as the posting service wants it, in the base currency.</summary>
    private static PostableDocument ToPostable(SalesInvoice i, decimal rate) =>
        new(DocumentTypes.SalesInvoice, i.Id, i.Number, i.Date, i.Description,
            i.Lines.Select(l => new DocumentLine(l.Description,
                rate == 1m ? l.Net : ExchangeRate.Convert(l.Net, rate), l.VatRate, l.IsStock)).ToList());

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
