namespace Stockia.Web.Domain;

/// <summary>SR-10 / UC-5: an invoice to a customer, for stock items, services or both. Number is the company's own
/// numbering, unique in the company (db/013). Stock lines leave from WarehouseId (the stock movement is SR-15).</summary>
public class SalesInvoice : Invoice
{
}
