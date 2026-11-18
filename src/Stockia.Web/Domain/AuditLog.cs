namespace Stockia.Web.Domain;

/// <summary>SR-6: one line of the audit trail: who did what, and when, optionally to one journal entry.</summary>
public class AuditLog
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    public int? EntryId { get; set; }
    public JournalEntry? Entry { get; set; }
    public DateTime LogTime { get; set; }
    public string Action { get; set; } = "";
    public string? Details { get; set; }
}