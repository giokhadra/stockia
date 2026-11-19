using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>SR-4 task 13.2 (d2, written first): the posting rules of sales and purchase invoices, with no database.
/// d6: amounts are decimals, VAT is rounded line by line and the entry balances to 0.01.</summary>
public class PostingRulesTests
{
    private static PostableDocument Invoice(string type, params DocumentLine[] lines) =>
        new(type, 1, "INV-1", new DateTime(2026, 11, 18), null, lines);

    private static decimal Debits(IEnumerable<PostingLine> lines) => Money.Total(lines.Select(l => l.Debit));
    private static decimal Credits(IEnumerable<PostingLine> lines) => Money.Total(lines.Select(l => l.Credit));

    private static PostingLine Only(IEnumerable<PostingLine> lines, PostingAccount account) =>
        Assert.Single(lines, l => l.Account == account);

    // ---- sales invoice: Dr receivable, Cr sales, Cr VAT collected ----

    [Fact]
    public void A_sales_invoice_debits_receivable_and_credits_sales_and_VAT()
    {
        var lines = new SalesInvoiceRule().Build(Invoice(DocumentTypes.SalesInvoice,
            new DocumentLine("Brake pads", 100m, 11m, true),
            new DocumentLine("Engine oil", 50m, 11m, true)));

        Assert.Equal(3, lines.Count);
        Assert.Equal(166.50m, Only(lines, PostingAccount.Receivable).Debit);
        Assert.Equal(150m, Only(lines, PostingAccount.Sales).Credit);
        Assert.Equal(16.50m, Only(lines, PostingAccount.VatCollected).Credit);
        Assert.Equal(Debits(lines), Credits(lines));
    }

    [Fact]
    public void A_sales_invoice_with_no_VAT_has_no_VAT_line()
    {
        var lines = new SalesInvoiceRule().Build(Invoice(DocumentTypes.SalesInvoice,
            new DocumentLine("Export part", 80m, 0m, true)));

        Assert.Equal(2, lines.Count);
        Assert.DoesNotContain(lines, l => l.Account == PostingAccount.VatCollected);
        Assert.Equal(80m, Only(lines, PostingAccount.Receivable).Debit);
        Assert.Equal(80m, Only(lines, PostingAccount.Sales).Credit);
    }

    [Fact]
    public void A_sales_invoice_with_mixed_VAT_rates_adds_each_lines_VAT()
    {
        var lines = new SalesInvoiceRule().Build(Invoice(DocumentTypes.SalesInvoice,
            new DocumentLine("Taxed part", 100m, 11m, true),
            new DocumentLine("Zero-rated part", 20m, 0m, true),
            new DocumentLine("Fitting service", 30m, 11m, false)));

        Assert.Equal(14.30m, Only(lines, PostingAccount.VatCollected).Credit);   // 11.00 + 0 + 3.30
        Assert.Equal(164.30m, Only(lines, PostingAccount.Receivable).Debit);
        Assert.Equal(Debits(lines), Credits(lines));
    }

    // d6: VAT is rounded per line, and the entry still balances
    [Fact]
    public void VAT_is_rounded_line_by_line_and_the_entry_balances_to_0_01()
    {
        // each line: 10.05 * 11% = 1.1055 -> 1.11; three lines -> 3.33 (11% of the 30.15 total would be 3.32)
        var line = new DocumentLine("Small part", 10.05m, 11m, true);
        var lines = new SalesInvoiceRule().Build(Invoice(DocumentTypes.SalesInvoice, line, line, line));

        Assert.Equal(3.33m, Only(lines, PostingAccount.VatCollected).Credit);
        Assert.Equal(30.15m, Only(lines, PostingAccount.Sales).Credit);
        Assert.Equal(33.48m, Only(lines, PostingAccount.Receivable).Debit);
        Assert.Equal(Debits(lines), Credits(lines));
    }

    [Fact]
    public void A_net_amount_with_more_than_two_decimals_is_rounded_like_everywhere_else()
    {
        var lines = new SalesInvoiceRule().Build(Invoice(DocumentTypes.SalesInvoice,
            new DocumentLine("A", 0.105m, 0m, true), new DocumentLine("B", 0.105m, 0m, true), new DocumentLine("C", 0.105m, 0m, true)));

        Assert.Equal(0.33m, Only(lines, PostingAccount.Sales).Credit);          // 3 x 0.11, as shown line by line
        Assert.Equal(Debits(lines), Credits(lines));
    }

    [Fact]
    public void A_sales_invoice_without_lines_gives_no_posting_lines()
    {
        Assert.Empty(new SalesInvoiceRule().Build(Invoice(DocumentTypes.SalesInvoice)));
    }

    // ---- purchase invoice: Dr stock or purchases, Dr VAT deductible, Cr payable ----

    [Fact]
    public void A_purchase_invoice_of_stock_items_debits_stock_and_VAT_and_credits_payable()
    {
        var lines = new PurchaseInvoiceRule().Build(Invoice(DocumentTypes.PurchaseInvoice,
            new DocumentLine("Brake pads", 200m, 11m, true)));

        Assert.Equal(3, lines.Count);
        Assert.Equal(200m, Only(lines, PostingAccount.Stock).Debit);
        Assert.Equal(22m, Only(lines, PostingAccount.VatDeductible).Debit);
        Assert.Equal(222m, Only(lines, PostingAccount.Payable).Credit);
        Assert.DoesNotContain(lines, l => l.Account == PostingAccount.Purchases);
        Assert.Equal(Debits(lines), Credits(lines));
    }

    [Fact]
    public void A_purchase_invoice_of_a_service_goes_to_purchases_not_stock()
    {
        var lines = new PurchaseInvoiceRule().Build(Invoice(DocumentTypes.PurchaseInvoice,
            new DocumentLine("Transport", 30m, 11m, false)));

        Assert.Equal(30m, Only(lines, PostingAccount.Purchases).Debit);
        Assert.DoesNotContain(lines, l => l.Account == PostingAccount.Stock);
        Assert.Equal(33.30m, Only(lines, PostingAccount.Payable).Credit);
    }

    [Fact]
    public void A_mixed_purchase_invoice_splits_stock_and_purchases_and_owes_the_total()
    {
        var lines = new PurchaseInvoiceRule().Build(Invoice(DocumentTypes.PurchaseInvoice,
            new DocumentLine("Brake pads", 200m, 11m, true),
            new DocumentLine("Transport", 30m, 11m, false)));

        Assert.Equal(200m, Only(lines, PostingAccount.Stock).Debit);
        Assert.Equal(30m, Only(lines, PostingAccount.Purchases).Debit);
        Assert.Equal(25.30m, Only(lines, PostingAccount.VatDeductible).Debit);   // 22.00 + 3.30
        Assert.Equal(255.30m, Only(lines, PostingAccount.Payable).Credit);
        Assert.Equal(Debits(lines), Credits(lines));
    }

    [Fact]
    public void A_purchase_invoice_with_no_VAT_has_no_VAT_line()
    {
        var lines = new PurchaseInvoiceRule().Build(Invoice(DocumentTypes.PurchaseInvoice,
            new DocumentLine("Imported part", 75m, 0m, true)));

        Assert.DoesNotContain(lines, l => l.Account == PostingAccount.VatDeductible);
        Assert.Equal(75m, Only(lines, PostingAccount.Payable).Credit);
    }

    [Fact]
    public void A_purchase_invoice_balances_to_0_01_with_rounded_VAT()
    {
        var line = new DocumentLine("Small part", 10.05m, 11m, true);
        var lines = new PurchaseInvoiceRule().Build(Invoice(DocumentTypes.PurchaseInvoice, line, line, line));

        Assert.Equal(3.33m, Only(lines, PostingAccount.VatDeductible).Debit);
        Assert.Equal(33.48m, Only(lines, PostingAccount.Payable).Credit);
        Assert.Equal(Debits(lines), Credits(lines));
    }

    // ---- which rule handles which document ----

    [Fact]
    public void Each_rule_handles_its_own_document_type()
    {
        Assert.Equal(DocumentTypes.SalesInvoice, new SalesInvoiceRule().DocumentType);
        Assert.Equal(DocumentTypes.PurchaseInvoice, new PurchaseInvoiceRule().DocumentType);
    }

    [Fact]
    public void Every_line_has_either_a_debit_or_a_credit_never_both()
    {
        var doc = Invoice(DocumentTypes.SalesInvoice, new DocumentLine("Part", 100m, 11m, true));
        var all = new SalesInvoiceRule().Build(doc)
            .Concat(new PurchaseInvoiceRule().Build(doc with { Type = DocumentTypes.PurchaseInvoice }));

        Assert.All(all, l => Assert.True((l.Debit > 0m) ^ (l.Credit > 0m)));
    }
}
