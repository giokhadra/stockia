namespace Stockia.Web.Domain;

/// <summary>SR-7: someone a company does business with. Suppliers exist now; customers follow with the sales invoices.
/// Balance is what the company owes the partner (positive) in the company's base currency (SR-39: DECIMAL(18,2)).</summary>
public abstract class Partner
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public string Name { get; set; } = "";
    public string? TaxNumber { get; set; }
    public string? Phone { get; set; }
    public decimal Balance { get; set; }
}
