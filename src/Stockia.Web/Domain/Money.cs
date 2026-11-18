namespace Stockia.Web.Domain;

/// <summary>SR-39: amounts are decimal with 2 places; a total is the sum of the rounded lines,
/// so the total always equals what the user sees line by line (to 0.01).</summary>
public static class Money
{
    public static decimal Round(decimal amount) =>
        Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    public static decimal Total(IEnumerable<decimal> lines) =>
        lines.Sum(Round);
}
