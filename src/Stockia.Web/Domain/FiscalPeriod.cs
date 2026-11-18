namespace Stockia.Web.Domain;

/// <summary>SR-18 / UC-14: one month of a company's books. Every new company gets the twelve
/// months of the current year; SR-5 later closes and reopens them.</summary>
public class FiscalPeriod
{
    public const string Open = "Open";
    public const string Closed = "Closed";

    public int Id { get; set; }
    public int CompanyId { get; set; }
    public Company? Company { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string Status { get; set; } = Open;

    /// <summary>The twelve monthly periods of a calendar year (the Lebanese fiscal year), all open.</summary>
    public static List<FiscalPeriod> MonthsOf(Company company, int year) =>
        Enumerable.Range(1, 12).Select(month =>
        {
            var start = new DateOnly(year, month, 1);
            return new FiscalPeriod { Company = company, StartDate = start, EndDate = start.AddMonths(1).AddDays(-1) };
        }).ToList();
}
