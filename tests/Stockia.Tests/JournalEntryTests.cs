using Stockia.Web.Domain;
using Xunit;

namespace Stockia.Tests;

/// <summary>SR-2 / d2, written before the code (task 7.1): the JournalEntry keeps any number of lines.</summary>
public class JournalEntryTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 3)]
    [InlineData(5, 4)]
    [InlineData(20, 20)]
    public void Entry_keeps_any_number_of_debit_and_credit_lines(int debits, int credits)
    {
        var entry = new JournalEntry();
        for (var i = 0; i < debits; i++) entry.AddLine(accountId: 1, debit: 10m, credit: 0m);
        for (var i = 0; i < credits; i++) entry.AddLine(accountId: 2, debit: 0m, credit: 10m);

        Assert.Equal(debits + credits, entry.Lines.Count);
        Assert.Equal(debits, entry.Lines.Count(l => l.Debit > 0));
        Assert.Equal(credits, entry.Lines.Count(l => l.Credit > 0));
    }

    [Fact]
    public void Entry_keeps_date_reference_and_description()
    {
        var entry = new JournalEntry { Date = new DateTime(2026, 11, 18), Reference = "JE-1", Description = "Pay rent" };
        Assert.Equal(new DateTime(2026, 11, 18), entry.Date);
        Assert.Equal("JE-1", entry.Reference);
        Assert.Equal("Pay rent", entry.Description);
    }

    [Fact]
    public void A_line_has_a_debit_or_a_credit_not_both()
    {
        var entry = new JournalEntry();
        Assert.Throws<ArgumentException>(() => entry.AddLine(1, 5m, 5m));
    }

    [Fact]
    public void A_line_needs_an_amount()
    {
        var entry = new JournalEntry();
        Assert.Throws<ArgumentException>(() => entry.AddLine(1, 0m, 0m));
    }

    [Fact]
    public void A_line_cannot_be_negative()
    {
        var entry = new JournalEntry();
        Assert.Throws<ArgumentException>(() => entry.AddLine(1, -5m, 0m));
        Assert.Throws<ArgumentException>(() => entry.AddLine(1, 0m, -5m));
    }

    // d6 / SR-39: line amounts are rounded to 0.01 and the totals are the sum of the rounded lines
    [Fact]
    public void Totals_equal_the_sum_of_the_rounded_lines_and_match_to_0_01()
    {
        var entry = new JournalEntry();
        entry.AddLine(1, 0.105m, 0m);   // shows as 0.11
        entry.AddLine(2, 0.105m, 0m);   // shows as 0.11
        entry.AddLine(3, 0.105m, 0m);   // shows as 0.11
        entry.AddLine(4, 0m, 0.33m);

        Assert.Equal(0.33m, entry.TotalDebit);
        Assert.Equal(0.33m, entry.TotalCredit);
        Assert.True(entry.IsBalanced);
    }

    [Fact]
    public void Entry_is_not_balanced_when_totals_differ_by_0_01_or_more()
    {
        var entry = new JournalEntry();
        entry.AddLine(1, 100.00m, 0m);
        entry.AddLine(2, 0m, 99.99m);
        Assert.False(entry.IsBalanced);
    }

    [Fact]
    public void Lines_carry_the_company_of_the_entry()
    {
        var entry = new JournalEntry { CompanyId = 7 };
        var line = entry.AddLine(1, 10m, 0m);
        Assert.Equal(7, line.CompanyId);
    }
}
