using Stockia.Web.Domain;
using Xunit;

namespace Stockia.Tests;

/// <summary>SR-2 / d2: each line is added to the ledger of its account.</summary>
public class JournalLedgerTests
{
    private static JournalEntry Entry(int id, DateTime date, string reference,
        params (int account, decimal debit, decimal credit)[] lines)
    {
        var e = new JournalEntry { Id = id, Date = date, Reference = reference, Description = "d-" + reference };
        foreach (var l in lines) e.AddLine(l.account, l.debit, l.credit);
        return e;
    }

    [Fact]
    public void Each_line_is_added_to_the_ledger_of_its_account()
    {
        var e = Entry(1, new DateTime(2026, 11, 18), "JE-1", (10, 100m, 0m), (20, 0m, 60m), (30, 0m, 40m));

        var l10 = Ledger.Build(10, new[] { e });
        var l20 = Ledger.Build(20, new[] { e });
        var l30 = Ledger.Build(30, new[] { e });

        Assert.Single(l10);
        Assert.Equal(100m, l10[0].Debit);
        Assert.Single(l20);
        Assert.Equal(60m, l20[0].Credit);
        Assert.Single(l30);
        Assert.Equal(40m, l30[0].Credit);
        Assert.All(new[] { l10[0], l20[0], l30[0] }, r => Assert.Equal("JE-1", r.Reference));
    }

    [Fact]
    public void An_account_the_entry_does_not_use_gets_nothing()
    {
        var e = Entry(1, new DateTime(2026, 11, 18), "JE-1", (10, 100m, 0m), (20, 0m, 100m));
        Assert.Empty(Ledger.Build(99, new[] { e }));
    }

    [Fact]
    public void Two_lines_of_the_same_account_in_one_entry_both_appear()
    {
        var e = Entry(1, new DateTime(2026, 11, 18), "JE-1", (10, 70m, 0m), (10, 30m, 0m), (20, 0m, 100m));
        var rows = Ledger.Build(10, new[] { e });
        Assert.Equal(2, rows.Count);
        Assert.Equal(100m, rows[1].Balance);
    }

    [Fact]
    public void Rows_are_ordered_by_date_with_a_running_balance_debit_minus_credit()
    {
        var later = Entry(2, new DateTime(2026, 11, 20), "JE-2", (10, 0m, 30m), (20, 30m, 0m));
        var earlier = Entry(1, new DateTime(2026, 11, 18), "JE-1", (10, 100m, 0m), (20, 0m, 100m));

        var rows = Ledger.Build(10, new[] { later, earlier });   // given out of order

        Assert.Equal(new[] { "JE-1", "JE-2" }, rows.Select(r => r.Reference));
        Assert.Equal(new[] { 100m, 70m }, rows.Select(r => r.Balance));
    }
}
