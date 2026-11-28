using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;

namespace Stockia.Web.Services;

/// <summary>The accounts a posting rule talks about. A rule names a role; the company's own account is found by
/// its code (PostingAccountCodes), so a rule never holds an account id.</summary>
public enum PostingAccount { Receivable, Payable, Sales, Purchases, Stock, VatCollected, VatDeductible, Cash, Bank }

/// <summary>SR-4: the code each role has in the chart of accounts (SR-1). Every company needs these accounts,
/// active, before a document can be posted; change a code here if the company's chart differs.</summary>
public static class PostingAccountCodes
{
    public static string CodeOf(PostingAccount account) => account switch
    {
        PostingAccount.Receivable => "411",
        PostingAccount.Payable => "401",
        PostingAccount.Sales => "701",
        PostingAccount.Purchases => "601",
        PostingAccount.Stock => "311",
        PostingAccount.VatCollected => "4457",
        PostingAccount.VatDeductible => "4456",
        PostingAccount.Cash => "531",          // SR-8, SR-11: cash box (PCGL class 5)
        PostingAccount.Bank => "512",          // SR-8, SR-11: bank (PCGL class 5)
        _ => throw new ArgumentOutOfRangeException(nameof(account))
    };

    public static string NameOf(PostingAccount account) => account switch
    {
        PostingAccount.Receivable => "receivable",
        PostingAccount.Payable => "payable",
        PostingAccount.Sales => "sales",
        PostingAccount.Purchases => "purchases",
        PostingAccount.Stock => "stock",
        PostingAccount.VatCollected => "VAT collected",
        PostingAccount.VatDeductible => "VAT deductible",
        PostingAccount.Cash => "cash",
        PostingAccount.Bank => "bank",
        _ => throw new ArgumentOutOfRangeException(nameof(account))
    };
}

/// <summary>One line of a document: a net amount (before VAT), its VAT rate in percent, and whether the item is held in stock.</summary>
public record DocumentLine(string Description, decimal Net, decimal VatRate, bool IsStock);

/// <summary>What the posting service needs to know about a saved document (a sales or purchase invoice, a receipt
/// or a payment). Number becomes the reference of the entry. CashAccount, for a receipt or a payment, is where the
/// money went in or out (Cash or Bank); for a receipt or payment each line is one invoice settled, VAT 0.</summary>
public record PostableDocument(string Type, int Id, string Number, DateTime Date, string? Description,
    IReadOnlyList<DocumentLine> Lines, PostingAccount? CashAccount = null);

/// <summary>A line of the entry a rule wants: a role, and either a debit or a credit.</summary>
public record PostingLine(PostingAccount Account, decimal Debit, decimal Credit);

/// <summary>The accounting of one kind of document. A rule is pure: it knows nothing about the database.</summary>
public interface IPostingRule
{
    string DocumentType { get; }
    IReadOnlyList<PostingLine> Build(PostableDocument document);
}

/// <summary>Tells whether a document already has its entry. Every method takes the companyId (SR-33).</summary>
public interface IPostedDocuments
{
    Task<int?> FindEntryIdAsync(int companyId, string documentType, int documentId);
}

public class EfPostedDocuments : IPostedDocuments
{
    private readonly StockiaDbContext _db;
    public EfPostedDocuments(StockiaDbContext db) => _db = db;

    public Task<int?> FindEntryIdAsync(int companyId, string documentType, int documentId) =>
        _db.JournalEntries
            .Where(e => e.CompanyId == companyId && e.SourceType == documentType && e.SourceId == documentId)
            .Select(e => (int?)e.Id)
            .FirstOrDefaultAsync();
}

/// <summary>SR-4: turns a saved document into a balanced journal entry linked to it, so nobody types the entry again.
/// The entry goes through JournalService.PostAsync, so it gets exactly the checks of a manual entry (SR-1 active
/// accounts, SR-2 lines, SR-3 balance). Every method takes the companyId of the signed-in user (SR-33).</summary>
public class PostingService
{
    private readonly JournalService _journal;
    private readonly IPostedDocuments _posted;
    private readonly IReadOnlyList<IPostingRule> _rules;
    private readonly AuditService _audit;

    public PostingService(JournalService journal, IPostedDocuments posted, IEnumerable<IPostingRule> rules, AuditService audit)
    {
        _journal = journal;
        _posted = posted;
        _rules = rules.ToList();
        _audit = audit;
    }

    public async Task<PostResult> PostAsync(int companyId, int userId, PostableDocument document)
    {
        var rule = _rules.FirstOrDefault(r => r.DocumentType == document.Type);
        if (rule is null)
            return PostResult.Fail(new[] { $"There is no posting rule for documents of type {document.Type}." });

        if (await _posted.FindEntryIdAsync(companyId, document.Type, document.Id) is int existing)
            return PostResult.Fail(new[] { AlreadyPosted(document, existing) });

        // The company's own accounts, found by code (active or not: an inactive one is refused by the journal checks).
        var wanted = rule.Build(document);
        var byCode = (await _journal.ListAccountsAsync(companyId))
            .GroupBy(a => a.Code).ToDictionary(g => g.Key, g => g.First());

        var missing = wanted.Select(w => w.Account).Distinct()
            .Where(role => !byCode.ContainsKey(PostingAccountCodes.CodeOf(role)))
            .Select(role => $"Account {PostingAccountCodes.CodeOf(role)} ({PostingAccountCodes.NameOf(role)}) " +
                            "does not exist in the chart of accounts of this company. Create it first.")
            .ToList();
        if (missing.Count > 0) return PostResult.Fail(missing);

        var lines = wanted
            .Select(w => new NewJournalLine(byCode[PostingAccountCodes.CodeOf(w.Account)].Id, w.Debit, w.Credit))
            .ToList();
        var input = new NewJournalEntry(document.Date, document.Number, Describe(document), lines,
            document.Type, document.Id);

        PostResult result;
        try
        {
            result = await _journal.PostAsync(companyId, input);
        }
        catch (DbUpdateException)
        {
            // two saves of the same document at the same moment: the table lets only one entry through
            return PostResult.Fail(new[] { $"{Label(document.Type)} {document.Number} has already been posted." });
        }
        if (!result.Succeeded) return result;

        // SR-6: who caused the entry, and when
        await _audit.RecordAsync(companyId, userId, AuditActions.EntryPostedAutomatically,
            $"From {Label(document.Type).ToLowerInvariant()} {document.Number}", result.EntryId);
        return result;
    }

    private static string AlreadyPosted(PostableDocument document, int entryId) =>
        $"{Label(document.Type)} {document.Number} has already been posted (journal entry {entryId}).";

    private static string Describe(PostableDocument document)
    {
        var text = $"{Label(document.Type)} {document.Number}";
        if (!string.IsNullOrWhiteSpace(document.Description)) text += ": " + document.Description.Trim();
        return text.Length > 500 ? text[..500] : text;
    }

    private static string Label(string documentType) => documentType switch
    {
        DocumentTypes.SalesInvoice => "Sales invoice",
        DocumentTypes.PurchaseInvoice => "Purchase invoice",
        DocumentTypes.Payment => "Payment",
        DocumentTypes.Receipt => "Receipt",
        _ => documentType
    };
}
