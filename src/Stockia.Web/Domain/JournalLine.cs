namespace Stockia.Web.Domain;

/// <summary>One debit or credit line of a journal entry. Create it with JournalEntry.AddLine.</summary>
public class JournalLine
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int JournalEntryId { get; set; }
    public int AccountId { get; set; }
    public Account? Account { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }

    /// <summary>SR-19: set only on a line entered in a foreign currency: the currency, the amount in it and the
    /// rate used. Debit or Credit always holds the base amount, so ledgers and totals stay in the base currency.</summary>
    public string? Currency { get; set; }
    public decimal? ForeignAmount { get; set; }
    public decimal? Rate { get; set; }
}
