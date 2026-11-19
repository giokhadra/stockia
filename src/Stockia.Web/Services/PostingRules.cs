using Stockia.Web.Domain;

namespace Stockia.Web.Services;

/// <summary>SR-4 task 13.2: shared arithmetic of the invoice rules. VAT is worked out line by line and rounded
/// (SR-39), and every total is the sum of the rounded lines (Money.Total), so the entry always balances to 0.01.</summary>
internal static class InvoiceMath
{
    public static decimal Vat(DocumentLine line) => Money.Round(Money.Round(line.Net) * line.VatRate / 100m);

    public static decimal Net(IEnumerable<DocumentLine> lines) => Money.Total(lines.Select(l => l.Net));

    public static decimal Vat(IEnumerable<DocumentLine> lines) => Money.Total(lines.Select(Vat));

    public static void Debit(List<PostingLine> to, PostingAccount account, decimal amount)
    {
        if (amount != 0m) to.Add(new PostingLine(account, amount, 0m));
    }

    public static void Credit(List<PostingLine> to, PostingAccount account, decimal amount)
    {
        if (amount != 0m) to.Add(new PostingLine(account, 0m, amount));
    }
}

/// <summary>A sales invoice: the customer owes the total (Dr receivable); the net is revenue (Cr sales) and the
/// VAT is owed to the state (Cr VAT collected). A line with no VAT adds no VAT line.</summary>
public class SalesInvoiceRule : IPostingRule
{
    public string DocumentType => DocumentTypes.SalesInvoice;

    public IReadOnlyList<PostingLine> Build(PostableDocument document)
    {
        var net = InvoiceMath.Net(document.Lines);
        var vat = InvoiceMath.Vat(document.Lines);

        var lines = new List<PostingLine>();
        InvoiceMath.Debit(lines, PostingAccount.Receivable, net + vat);
        InvoiceMath.Credit(lines, PostingAccount.Sales, net);
        InvoiceMath.Credit(lines, PostingAccount.VatCollected, vat);
        return lines;
    }
}

/// <summary>A purchase invoice: we owe the supplier the total (Cr payable). The net of stock items goes to stock,
/// the net of everything else (services, transport) to purchases, and the VAT we can recover to VAT deductible.</summary>
public class PurchaseInvoiceRule : IPostingRule
{
    public string DocumentType => DocumentTypes.PurchaseInvoice;

    public IReadOnlyList<PostingLine> Build(PostableDocument document)
    {
        var stockNet = InvoiceMath.Net(document.Lines.Where(l => l.IsStock));
        var otherNet = InvoiceMath.Net(document.Lines.Where(l => !l.IsStock));
        var vat = InvoiceMath.Vat(document.Lines);

        var lines = new List<PostingLine>();
        InvoiceMath.Debit(lines, PostingAccount.Stock, stockNet);
        InvoiceMath.Debit(lines, PostingAccount.Purchases, otherNet);
        InvoiceMath.Debit(lines, PostingAccount.VatDeductible, vat);
        InvoiceMath.Credit(lines, PostingAccount.Payable, stockNet + otherNet + vat);
        return lines;
    }
}
