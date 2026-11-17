using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;

namespace Stockia.Web.Services;

/// <summary>SR-18: create companies, each with its own books and base currency.</summary>
public class CompanyService
{
    public static readonly string[] Currencies = { "LBP", "USD", "EUR" };

    private readonly StockiaDbContext _db;
    public CompanyService(StockiaDbContext db) => _db = db;

    public Task<List<Company>> ListAsync() =>
        _db.Companies.OrderBy(c => c.Name).ToListAsync();

    public async Task<(Company? Company, string? Error)> CreateAsync(
        string? name, string? taxNumber, string? baseCurrency)
    {
        if (string.IsNullOrWhiteSpace(name))
            return (null, "The company name is required.");
        if (baseCurrency is null || !Currencies.Contains(baseCurrency))
            return (null, "Choose a base currency from the list.");

        var company = new Company
        {
            Name = name.Trim(),
            TaxNumber = string.IsNullOrWhiteSpace(taxNumber) ? null : taxNumber.Trim(),
            BaseCurrency = baseCurrency
        };
        _db.Companies.Add(company);
        await _db.SaveChangesAsync();
        return (company, null);
    }
}