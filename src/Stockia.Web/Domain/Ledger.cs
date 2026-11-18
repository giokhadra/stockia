namespace Stockia.Web.Domain;

public sealed record LedgerRow(DateTime Date, int EntryId, string Reference, string Description,
    decimal Debit, decimal Credit, decimal Balance);

/// <summary>The ledger of an account is built from the journal lines posted to it (no separate copy to keep in sync).</summary>
public static class Ledger
{
    /// <summary>Rows of one account, oldest first, with a running balance (debit minus credit).</summary>
    public static IReadOnlyList<LedgerRow> Build(int accountId, IEnumerable<JournalEntry> entries)
    {
        var rows = new List<LedgerRow>();
        decimal balance = 0m;
        foreach (var entry in entries.OrderBy(e => e.Date).ThenBy(e => e.Id))
        {
            foreach (var line in entry.Lines.Where(l => l.AccountId == accountId))
            {
                balance = Money.Round(balance + line.Debit - line.Credit);
                rows.Add(new LedgerRow(entry.Date, entry.Id, entry.Reference, entry.Description,
                    line.Debit, line.Credit, balance));
            }
        }
        return rows;
    }
}
