namespace Stockia.Web.Domain;

public class Account
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int? ParentId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "Asset";
    public bool IsActive { get; set; } = true;
}