using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Security;

namespace Stockia.Web.Services;

/// <summary>SR-18: create companies, each with its own books, base currency and fiscal periods.</summary>
public class CompanyService
{
    public static readonly string[] Currencies = { "LBP", "USD", "EUR" };

    private readonly StockiaDbContext _db;
    private readonly TimeProvider _clock;

    public CompanyService(StockiaDbContext db) : this(db, TimeProvider.System) { }

    public CompanyService(StockiaDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public Task<List<Company>> ListAsync() =>
        _db.Companies.OrderBy(c => c.Name).ToListAsync();

    /// <summary>SR-20: a user sees only the companies assigned to them.</summary>
    public Task<List<Company>> ListForUserAsync(int userId) =>
        _db.Assignments.Where(a => a.UserId == userId).Select(a => a.Company!)
            .OrderBy(c => c.Name).ToListAsync();

    public ValueTask<Company?> FindAsync(int id) => _db.Companies.FindAsync(id);

    /// <summary>The periods of one company only, oldest first.</summary>
    public Task<List<FiscalPeriod>> PeriodsOfAsync(int companyId) =>
        _db.FiscalPeriods.Where(p => p.CompanyId == companyId).OrderBy(p => p.StartDate).ToListAsync();

    /// <summary>Number of fiscal periods per company id.</summary>
    public Task<Dictionary<int, int>> PeriodCountsAsync() =>
        _db.FiscalPeriods.GroupBy(p => p.CompanyId)
            .Select(g => new { CompanyId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CompanyId, x => x.Count);

    public Task<Dictionary<int, int>> PeriodCountsForUserAsync(int userId)
    {
        var companyIds = _db.Assignments.Where(a => a.UserId == userId).Select(a => a.CompanyId);
        return _db.FiscalPeriods.Where(p => companyIds.Contains(p.CompanyId))
            .GroupBy(p => p.CompanyId)
            .Select(g => new { CompanyId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CompanyId, x => x.Count);
    }

    public async Task<(Company? Company, string? Error)> CreateAsync(
        string? name, string? taxNumber, string? baseCurrency, int? creatorUserId = null)
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
        // The company and its twelve periods are saved together, in one SaveChanges.
        _db.FiscalPeriods.AddRange(FiscalPeriod.MonthsOf(company, _clock.GetUtcNow().Year));

        if (creatorUserId is not null)
        {
            var administrator = await _db.Roles.FirstOrDefaultAsync(r => r.Name == CompanyRoles.Administrator);
            if (administrator is not null)
                _db.Assignments.Add(new Assignment
                {
                    UserId = creatorUserId.Value, Company = company, RoleId = administrator.Id
                });
        }

        await _db.SaveChangesAsync();
        return (company, null);
    }
}
