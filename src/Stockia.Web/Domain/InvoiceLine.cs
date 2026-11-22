namespace Stockia.Web.Domain;

/// <summary>SR-7: one line of an invoice. The item's VAT rate is copied onto the line when the invoice is saved,
/// so a later change of the item's rate (BRD OI-8) never alters an old invoice.</summary>
public class InvoiceLine
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int InvoiceId { get; set; }
    public int ItemId { get; set; }
    public string Description { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal VatRate { get; set; }
    public bool IsStock { get; set; } = true;

    /// <summary>Before VAT, to the cent.</summary>
    public decimal Net => Money.Round(Quantity * UnitPrice);

    /// <summary>The VAT of this line, rounded on its own (same rule as the posting rules).</summary>
    public decimal Vat => Money.Round(Net * VatRate / 100m);

    public decimal Total => Net + Vat;
}
