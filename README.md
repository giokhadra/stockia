# Stockia (ERP accounting) - walking skeleton

## Setup
1. Install the .NET 8 SDK and SQL Server (Express/LocalDB/Developer) plus SSMS or `sqlcmd`.
2. Clone the repository.
3. Trust the development HTTPS certificate, once per machine: `dotnet dev-certs https --trust`
   (the app runs over HTTPS only, SR-34).
4. Create the database: run every script in `db/` in order, `001` to the highest number.
   `001` creates `StockiaDb`; the others add tables and can be run again safely.
   After every pull, run any script that is new. With `sqlcmd`:
   `sqlcmd -S localhost -E -C -i db/001_create_appuser.sql`, then
   `sqlcmd -S localhost -E -C -d StockiaDb -i db/002_company_v1.sql` (and so on for each later script).
5. Check `ConnectionStrings:StockiaDb` in `src/Stockia.Web/appsettings.json`
   (adjust `Server=` for your machine).
6. Build and test: `dotnet build` then `dotnet test`
   (tests use an in-memory database; the SQL Server tests are skipped unless `STOCKIA_TEST_CONNECTION` is set, as CI does).
7. Run: `dotnet run --project src/Stockia.Web` (or the `https` profile in Visual Studio) and open
   `https://localhost:7180`; `http://localhost:5180` redirects there.
8. Seeded development logins on the demo company: `admin` / `Admin#2026` (Administrator),
   `viewer` / `Viewer#2026` (Viewer) and `accountant` / `Accountant#2026` (Accountant),
   created on first start when missing.
   A password stored before bcrypt is re-hashed with bcrypt at its first successful login.
   After login a user with one assignment enters that company; a user with several chooses one.
   Every page and API checks the chosen company and the role held there; a refused request returns 403 (SR-33).

## Palette
`#D4AF37` gold, `#FFFFFF` white, `#1A1A1A` charcoal (CSS variables in `src/Stockia.Web/wwwroot/css/site.css`).

## Quality rules
See `docs/DEFINITION_OF_DONE.md`. CI runs the CREATE TABLE script on a SQL Server container and then the tests.
If you created `AppUser` with the first version of the script, drop `StockiaDb` and re-run `db/001_create_appuser.sql`
(the script now also creates `Company` and `AppUser.CompanyId`).

## Automatic posting (SR-4)
A saved document gets its journal entry automatically; nobody retypes it. Run `db/009_posting_v1.sql` after pulling
(it adds `SourceType` and `SourceId` to `JournalEntry`).

- `PostingService.PostAsync(companyId, userId, document)` turns a `PostableDocument` into a balanced entry linked to the
  document and posts it through `JournalService`, so it gets the checks of a manual entry (SR-1, SR-2, SR-3).
  A document is posted only once. The invoice pages call it right after they save the invoice.
- Rules (`PostingRules.cs`): a sales invoice is Dr receivable, Cr sales, Cr VAT collected; a purchase invoice is Dr stock
  (stock items) or purchases (the rest), Dr VAT deductible, Cr payable. VAT is rounded line by line (SR-39).
  Payments and receipts need their own `IPostingRule`: until then they are refused with a clear message.
- Every company needs these accounts, **active**, in its chart of accounts, found by code (`PostingAccountCodes`):
  411 receivable, 401 payable, 701 sales, 601 purchases, 311 stock, 4457 VAT collected, 4456 VAT deductible.
  A missing account is named in the error. Change a code in `PostingAccountCodes` if the company's chart differs.
