namespace Stockia.Web.Domain;

/// <summary>How money came in or went out: the cash box (531) or the bank (512).</summary>
public static class ReceiptMethods
{
    public const string Cash = "Cash";
    public const string Bank = "Bank";
    public static readonly string[] All = { Cash, Bank };
}

/// <summary>SR-8 / SR-11: money that settles invoices: a customer receipt (Kind 'Receipt', SR-11) or a supplier
/// payment (Kind 'Payment', SR-8). Amount is in the currency of the invoices it settles; BaseAmount is what it takes
/// off the partner's balance, in the base currency. A settlement is allocated to one or more invoices.</summary>
public abstract class Settlement
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int PartnerId { get; set; }
    public Partner? Partner { get; set; }
    public string Number { get; set; } = "";
    public DateTime Date { get; set; }
    public string Method { get; set; } = ReceiptMethods.Bank;
    public string Currency { get; set; } = "USD";
    public decimal Amount { get; set; }
    public decimal BaseAmount { get; set; }
    public string? Description { get; set; }
    public List<SettlementAllocation> Allocations { get; } = new();
}

/// <summary>SR-11 / UC-6: money received from a customer, allocated to their open sales invoices.</summary>
public class CustomerReceipt : Settlement
{
}

/// <summary>The part of a settlement that pays one invoice. Amount is in the invoice currency; BaseAmount is the share
/// of the invoice's base total it settles (the invoice's own rate, so a fully paid invoice is settled exactly).</summary>
public class SettlementAllocation
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int SettlementId { get; set; }
    public int InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public decimal Amount { get; set; }
    public decimal BaseAmount { get; set; }
}
