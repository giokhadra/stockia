namespace Stockia.Web.Domain;

public class Company
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? TaxNumber { get; set; }
    public string BaseCurrency { get; set; } = "USD";
}