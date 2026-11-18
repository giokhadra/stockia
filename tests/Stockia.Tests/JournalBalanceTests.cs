using Stockia.Web.Domain;
using Xunit;

namespace Stockia.Tests;

/// <summary>SR-3 / d2, written before the code: the entry knows the difference between its debits and credits.</summary>
public class JournalBalanceTests
{
    [Fact]
    public void Difference_is_total_debits_minus_total_credits()
    {
        var entry = new JournalEntry();
        entry.AddLine(1, 100.00m, 0m);
        entry.AddLine(2, 0m, 99.99m);

        Assert.Equal(0.01m, entry.Difference);
        Assert.False(entry.IsBalanced);
    }

    [Fact]
    public void Difference_is_negative_when_credits_are_higher()
    {
        var entry = new JournalEntry();
        entry.AddLine(1, 100.00m, 0m);
        entry.AddLine(2, 0m, 150.50m);

        Assert.Equal(-50.50m, entry.Difference);
    }

    [Fact]
    public void Difference_is_zero_when_the_entry_is_balanced()
    {
        var entry = new JournalEntry();
        entry.AddLine(1, 800m, 0m);
        entry.AddLine(2, 150m, 0m);
        entry.AddLine(3, 0m, 950m);

        Assert.Equal(0m, entry.Difference);
        Assert.True(entry.IsBalanced);
    }

    // d6 / SR-39: the difference is worked out from the rounded lines, to 0.01
    [Fact]
    public void Difference_uses_the_rounded_lines()
    {
        var entry = new JournalEntry();
        entry.AddLine(1, 0.105m, 0m);   // shows as 0.11
        entry.AddLine(2, 0.105m, 0m);   // shows as 0.11
        entry.AddLine(3, 0.105m, 0m);   // shows as 0.11
        entry.AddLine(4, 0m, 0.33m);

        Assert.Equal(0m, entry.Difference);
    }
}
