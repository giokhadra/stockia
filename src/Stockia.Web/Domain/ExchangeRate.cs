namespace Stockia.Web.Domain;

/// <summary>SR-19: the value of one unit of a foreign currency in the company's base currency, from a date on.
/// A rate applies from its own date until the next rate of the same currency. Rates keep 10 decimals
/// (DECIMAL(20,10)), so a small rate such as 1 LBP in USD (0.0000111732) is exact enough to the cent.</summary>
public class ExchangeRate
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public string Currency { get; set; } = "";
    public DateTime Date { get; set; }
    public decimal Rate { get; set; }

    public const int RateDecimals = 10;

    /// <summary>The latest rate of the currency on or before the date, or null when there is none.</summary>
    public static ExchangeRate? On(IEnumerable<ExchangeRate> rates, string currency, DateTime date) =>
        rates.Where(r => r.Currency == currency && r.Date.Date <= date.Date)
             .OrderByDescending(r => r.Date)
             .FirstOrDefault();

    /// <summary>The base amount of a foreign amount, to the cent (SR-39): the foreign amount has 2 decimals,
    /// the product is rounded once.</summary>
    public static decimal Convert(decimal foreignAmount, decimal rate) =>
        Money.Round(Money.Round(foreignAmount) * rate);

    /// <summary>Three letters A to Z (ISO 4217 style: USD, LBP, EUR).</summary>
    public static bool IsCurrencyCode(string? code) =>
        code is { Length: 3 } && code.All(c => c is >= 'A' and <= 'Z');
}
