using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;

namespace Stockia.Web.Services;

/// <summary>The amount typed against one open invoice.</summary>
public record ReceiptAllocationForm(int InvoiceId, decimal? Amount);

/// <summary>What the user types for a receipt. A blank number takes the next one (R-0001...).</summary>
public record ReceiptForm(int? CustomerId, string? Number, DateTime? Date, string? Method, string? Description,
    IReadOnlyList<ReceiptAllocationForm> Allocations);

/// <summary>A posted sales invoice with what has been received on it so far and what is still open.</summary>
public record OpenInvoice(SalesInvoice Invoice, decimal Paid, decimal Open);

/// <summary>SR-11 / UC-6: customer receipts of one company, allocated to the customer's open sales invoices. Every
/// method takes the companyId of the signed-in user (SR-33). Saving posts Dr Cash or Bank, Cr Receivable (SR-4)
/// and lowers the customer's balance, in one go: a receipt that cannot be posted is not kept.</summary>
public class ReceiptService
{
    public const string NumberPrefix = "R-";

    private readonly StockiaDbContext _db;
    private readonly PostingService _posting;

    public ReceiptService(StockiaDbContext db, PostingService posting)
    {
        _db = db;
        _posting = posting;
    }

    public Task<List<CustomerReceipt>> ListAsync(int companyId) =>
        _db.CustomerReceipts.Include(r => r.Partner)
            .Where(r => r.CompanyId == companyId)
            .OrderByDescending(r => r.Date).ThenByDescending(r => r.Id).ToListAsync();

    public Task<CustomerReceipt?> FindAsync(int companyId, int id) =>
        _db.CustomerReceipts.Include(r => r.Partner).Include(r => r.Allocations).ThenInclude(a => a.Invoice)
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == id);

    /// <summary>The customer's posted sales invoices that are not fully paid, oldest first. An invoice waiting for
    /// approval (SR-15) is not posted, so it cannot be paid yet.</summary>
    public async Task<List<OpenInvoice>> OpenInvoicesAsync(int companyId, int customerId)
    {
        var invoices = await _db.SalesInvoices
            .Where(i => i.CompanyId == companyId && i.PartnerId == customerId && i.Status == InvoiceStatuses.Posted)
            .OrderBy(i => i.Date).ThenBy(i => i.Id).ToListAsync();
        var ids = invoices.Select(i => i.Id).ToList();
        var paid = await _db.SettlementAllocations.Where(a => a.CompanyId == companyId && ids.Contains(a.InvoiceId))
            .GroupBy(a => a.InvoiceId).Select(g => new { g.Key, Sum = g.Sum(a => a.Amount) })
            .ToDictionaryAsync(x => x.Key, x => x.Sum);
        return invoices
            .Select(i => new OpenInvoice(i, paid.GetValueOrDefault(i.Id), i.Total - paid.GetValueOrDefault(i.Id)))
            .Where(o => o.Open > 0m)
            .ToList();
    }

    public async Task<string> NextNumberAsync(int companyId)
    {
        var numbers = await _db.CustomerReceipts.Where(r => r.CompanyId == companyId && r.Number.StartsWith(NumberPrefix))
            .Select(r => r.Number).ToListAsync();
        var last = numbers.Select(n => int.TryParse(n[NumberPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0)
            .DefaultIfEmpty(0).Max();
        return NumberPrefix + (last + 1).ToString("0000", CultureInfo.InvariantCulture);
    }

    public async Task<(CustomerReceipt? Receipt, IReadOnlyList<string> Errors)> CreateAsync(int companyId, int userId, ReceiptForm form)
    {
        var errors = new List<string>();

        var customer = form.CustomerId is int cid
            ? await _db.Customers.FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == cid)
            : null;
        if (customer is null) return (null, new[] { "Choose a customer of this company." });

        var number = string.IsNullOrWhiteSpace(form.Number) ? await NextNumberAsync(companyId) : form.Number.Trim();
        if (number.Length > 50) errors.Add("The receipt number can have at most 50 characters.");
        else if (await _db.CustomerReceipts.AnyAsync(r => r.CompanyId == companyId && r.Number == number))
            errors.Add($"Receipt {number} already exists in this company.");
        if (form.Date is null) errors.Add("The date is required.");
        var method = ReceiptMethods.All.FirstOrDefault(m => string.Equals(m, form.Method?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (method is null) errors.Add("The method is Cash or Bank.");
        var description = string.IsNullOrWhiteSpace(form.Description) ? null : form.Description.Trim();
        if (description is { Length: > 500 }) errors.Add("The description can have at most 500 characters.");

        var open = (await OpenInvoicesAsync(companyId, customer.Id)).ToDictionary(o => o.Invoice.Id);
        var receipt = new CustomerReceipt
        {
            CompanyId = companyId, PartnerId = customer.Id, Number = number,
            Date = form.Date?.Date ?? default, Method = method ?? "", Description = description
        };

        var n = 0;
        foreach (var a in form.Allocations)
        {
            n++;
            if (a.Amount is null || a.Amount == 0m) continue;          // a blank row pays nothing
            var amount = Money.Round(a.Amount.Value);
            if (amount < 0m) { errors.Add($"Line {n}: the amount cannot be negative."); continue; }
            if (!open.TryGetValue(a.InvoiceId, out var o))
            {
                errors.Add($"Line {n}: not an open invoice of this customer.");
                continue;
            }
            if (receipt.Allocations.Any(x => x.InvoiceId == a.InvoiceId))
            {
                errors.Add($"Line {n}: {o.Invoice.Number} appears twice.");
                continue;
            }
            if (amount > o.Open)
            {
                errors.Add($"{o.Invoice.Number}: only {o.Open.ToString("0.00", CultureInfo.InvariantCulture)} is open " +
                           $"({amount.ToString("0.00", CultureInfo.InvariantCulture)} typed).");
                continue;
            }
            receipt.Allocations.Add(new SettlementAllocation
            {
                CompanyId = companyId, InvoiceId = o.Invoice.Id, Invoice = o.Invoice, Amount = amount,
                BaseAmount = BaseShare(o, amount)
            });
        }
        if (receipt.Allocations.Count == 0 && errors.All(e => !e.StartsWith("Line") && !e.Contains("is open")))
            errors.Add("Enter an amount for at least one invoice.");
        var currencies = receipt.Allocations.Select(x => x.Invoice!.Currency).Distinct().ToList();
        if (currencies.Count > 1) errors.Add("All invoices of one receipt must be in the same currency.");
        if (errors.Count > 0) return (null, errors);

        receipt.Currency = currencies[0];
        receipt.Amount = Money.Total(receipt.Allocations.Select(x => x.Amount));
        receipt.BaseAmount = Money.Total(receipt.Allocations.Select(x => x.BaseAmount));

        _db.CustomerReceipts.Add(receipt);
        await _db.SaveChangesAsync();          // the receipt needs its Id to be posted

        var document = new PostableDocument(DocumentTypes.Receipt, receipt.Id, receipt.Number, receipt.Date, receipt.Description,
            receipt.Allocations.Select(x => new DocumentLine(x.Invoice!.Number, x.BaseAmount, 0m, false)).ToList(),
            method == ReceiptMethods.Cash ? PostingAccount.Cash : PostingAccount.Bank);
        var posted = await _posting.PostAsync(companyId, userId, document);
        if (!posted.Succeeded)
        {
            _db.CustomerReceipts.Remove(receipt);   // nothing is kept of a receipt that could not be posted
            await _db.SaveChangesAsync();
            return (null, posted.Errors);
        }

        customer.Balance -= receipt.BaseAmount;     // SR-11: what the customer owes falls by the receipt
        await _db.SaveChangesAsync();
        return (receipt, Array.Empty<string>());
    }

    /// <summary>The base amount an allocation settles: the invoice's own rate (BaseTotal / Total); the payment that
    /// closes the invoice takes exactly what is left, so a paid invoice is settled to the cent (SR-39).</summary>
    private decimal BaseShare(OpenInvoice o, decimal amount)
    {
        var inv = o.Invoice;
        if (inv.Total == inv.BaseTotal) return amount;
        if (amount == o.Open)
        {
            var baseSettled = _db.SettlementAllocations.Where(a => a.InvoiceId == inv.Id).Select(a => a.BaseAmount).ToList().Sum();
            return inv.BaseTotal - baseSettled;
        }
        return Money.Round(amount * inv.BaseTotal / inv.Total);
    }
}
