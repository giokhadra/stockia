namespace Stockia.Web.Domain;

/// <summary>SR-2: a manual journal entry with a date, a reference, a description and any number of
/// debit and credit lines. Amounts follow SR-39 (2 decimals, totals are sums of the rounded lines).</summary>
public class JournalEntry
{
    private readonly List<JournalLine> _lines = new();

    public int Id { get; set; }
    public int CompanyId { get; set; }
    public DateTime Date { get; set; }
    public string Reference { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        /// <summary>SR-6: set only on a reversal: the entry it reverses.</summary>
    public int? ReversesEntryId { get; set; }

    public IReadOnlyCollection<JournalLine> Lines => _lines;

    /// <summary>Adds a line; it takes the company of the entry. Exactly one of debit/credit must be above zero.</summary>
    public JournalLine AddLine(int accountId, decimal debit, decimal credit)
    {
        var d = Money.Round(debit);
        var c = Money.Round(credit);
        if (d < 0 || c < 0)
            throw new ArgumentException("A line amount cannot be negative.");
        if ((d == 0) == (c == 0))
            throw new ArgumentException("A line has either a debit or a credit amount, not both and not neither.");

        var line = new JournalLine { CompanyId = CompanyId, AccountId = accountId, Debit = d, Credit = c };
        _lines.Add(line);
        return line;
    }

    public decimal TotalDebit => Money.Total(_lines.Select(l => l.Debit));
    public decimal TotalCredit => Money.Total(_lines.Select(l => l.Credit));

    /// <summary>SR-3: total debits minus total credits (positive: debits are higher). Zero when the entry is balanced.</summary>
    public decimal Difference => TotalDebit - TotalCredit;

    /// <summary>Amounts have 2 decimals, so "match to 0.01" means the totals are equal.</summary>
    public bool IsBalanced => Difference == 0m;
}
