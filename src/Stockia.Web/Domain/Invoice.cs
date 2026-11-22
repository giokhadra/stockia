namespace Stockia.Web.Domain;

/// <summary>SR-7: a commercial invoice. Amounts are in the invoice currency; BaseTotal is the total in the company's
/// base currency (the same as Total when the currency is the base currency). Totals are always the sum of the
/// rounded lines (SR-39), so they match what the user sees line by line to 0.01.</summary>
public abstract class Invoice
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int PartnerId { get; set; }
    public Partner? Partner { get; set; }
    public string Number { get; set; } = "";
    public DateTime Date { get; set; }
    public DateTime DueDate { get; set; }
    public string Currency { get; set; } = "USD";
    public string? Description { get; set; }
    public int? WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    public List<InvoiceLine> Lines { get; } = new();

    public decimal Subtotal { get; set; }
    public decimal VatTotal { get; set; }
    public decimal Total { get; set; }
    public decimal BaseTotal { get; set; }

    /// <summary>Works out the totals from the lines: each line's net and VAT are rounded first, then summed.</summary>
    public void Recalculate()
    {
        Subtotal = Money.Total(Lines.Select(l => l.Net));
        VatTotal = Money.Total(Lines.Select(l => l.Vat));
        Total = Subtotal + VatTotal;
        BaseTotal = Total;
    }
}
