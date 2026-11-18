namespace Stockia.Web.Domain;

/// <summary>SR-13: a place where a company keeps stock items.</summary>
public class Warehouse
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public string Name { get; set; } = "";
}