namespace Stockia.Web.Domain;

public static class StockMovementTypes
{
    public const string In = "In";
    public const string Out = "Out";
    public const string Adjust = "Adjust";
}


public class StockMovement
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int ItemId { get; set; }
    public int WarehouseId { get; set; }
    public int? InvoiceLineId { get; set; }
    public DateTime Date { get; set; }
    public string Type { get; set; } = StockMovementTypes.In;
    public decimal Quantity { get; set; }
    public decimal Value { get; set; }
}