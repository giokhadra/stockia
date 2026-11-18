using Stockia.Web.Domain;

namespace Stockia.Web.Controllers;

public class JournalLineForm
{
    public int AccountId { get; set; }
    public decimal? Debit { get; set; }
    public decimal? Credit { get; set; }
}

/// <summary>What the browser posts (Date, Reference, Description, Lines[i].AccountId/Debit/Credit).</summary>
public class JournalEntryForm
{
    public DateTime Date { get; set; } = DateTime.Today;
    public string? Reference { get; set; }
    public string? Description { get; set; }
    public List<JournalLineForm> Lines { get; set; } = new();
}

public class JournalNewViewModel
{
    public JournalEntryForm Form { get; set; } = new();
    public List<Account> Accounts { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}
