namespace Stockia.Web.Domain;

/// <summary>SR-13: a product or service a company sells or buys. Only stock items (IsStock) have quantities.
/// Each item carries its own VAT rate (11, 0 or any rate the law sets), which can be edited.</summary>
public class Item
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";
    public string? Category { get; set; }
    public decimal Cost { get; set; }
    public decimal SalePrice { get; set; }
    public decimal VatRate { get; set; }
    public bool IsStock { get; set; } = true;
}