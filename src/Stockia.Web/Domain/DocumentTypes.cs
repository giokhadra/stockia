namespace Stockia.Web.Domain;

/// <summary>SR-4: the kinds of business document whose journal entry is posted automatically.
/// Stored in JournalEntry.SourceType (at most 30 characters).</summary>
public static class DocumentTypes
{
    public const string SalesInvoice = "SalesInvoice";
    public const string PurchaseInvoice = "PurchaseInvoice";
    public const string Payment = "Payment";
    public const string Receipt = "Receipt";
}
