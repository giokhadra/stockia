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
}
