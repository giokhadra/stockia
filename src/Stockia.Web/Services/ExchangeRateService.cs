using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;

namespace Stockia.Web.Services;

/// <summary>SR-19: what the journal needs to convert a foreign amount. Every method filters by company (SR-33).</summary>
public interface IExchangeRates
{
    Task<string> BaseCurrencyAsync(int companyId);
    Task<ExchangeRate?> RateOnAsync(int companyId, string currency, DateTime date);
}

/// <summary>SR-19 / UC-7: the exchange rates of a company.</summary>
public class ExchangeRateService : IExchangeRates
{
    private readonly StockiaDbContext _db;
    public ExchangeRateService(StockiaDbContext db) => _db = db;

    public async Task<string> BaseCurrencyAsync(int companyId) =>
        (await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.BaseCurrency).SingleAsync()).Trim();

    /// <summary>Newest date first, by currency.</summary>
    public Task<List<ExchangeRate>> ListAsync(int companyId) =>
        _db.ExchangeRates.AsNoTracking()
            .Where(r => r.CompanyId == companyId)
            .OrderBy(r => r.Currency).ThenByDescending(r => r.Date)
            .ToListAsync();

    /// <summary>The currencies that have at least one rate, for the journal page.</summary>
    public Task<List<string>> CurrenciesAsync(int companyId) =>
        _db.ExchangeRates.AsNoTracking()
            .Where(r => r.CompanyId == companyId)
            .Select(r => r.Currency).Distinct().OrderBy(c => c)
            .ToListAsync();

    public Task<ExchangeRate?> RateOnAsync(int companyId, string currency, DateTime date)
    {
        var day = date.Date;
        return _db.ExchangeRates.AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.Currency == currency && r.Date <= day)
            .OrderByDescending(r => r.Date)
            .FirstOrDefaultAsync();
    }

    /// <summary>Records the rate of a currency from a date on; a second rate on the same date replaces the first.
    /// Returns the reasons it was refused (empty when saved).</summary>
    public async Task<List<string>> SetAsync(int companyId, string? currency, DateTime date, decimal rate)
    {
        var errors = new List<string>();
        var code = currency?.Trim().ToUpperInvariant() ?? "";
        var value = Math.Round(rate, ExchangeRate.RateDecimals, MidpointRounding.AwayFromZero);

        if (!ExchangeRate.IsCurrencyCode(code))
            errors.Add("The currency is a three-letter code, such as LBP or EUR.");
        else if (code == await BaseCurrencyAsync(companyId))
            errors.Add($"{code} is the base currency of this company; its rate is always 1.");
        if (date == default) errors.Add("Date is required.");
        if (value <= 0) errors.Add("The rate must be above zero.");
        else if (value >= 10_000_000_000m) errors.Add("The rate is too large.");
        if (errors.Count > 0) return errors;

        var day = date.Date;
        var existing = await _db.ExchangeRates
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Currency == code && r.Date == day);
        if (existing is null)
            _db.ExchangeRates.Add(new ExchangeRate { CompanyId = companyId, Currency = code, Date = day, Rate = value });
        else
            existing.Rate = value;
        await _db.SaveChangesAsync();
        return errors;
    }
}
