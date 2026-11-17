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
8. Seeded development login: `admin` / `Admin#2026` (created on first start if AppUser is empty).
   A password stored before bcrypt is re-hashed with bcrypt at its first successful login.

## Palette
`#D4AF37` gold, `#FFFFFF` white, `#1A1A1A` charcoal (CSS variables in `src/Stockia.Web/wwwroot/css/site.css`).

## Quality rules
See `docs/DEFINITION_OF_DONE.md`. CI runs the CREATE TABLE script on a SQL Server container and then the tests.
If you created `AppUser` with the first version of the script, drop `StockiaDb` and re-run `db/001_create_appuser.sql`
(the script now also creates `Company` and `AppUser.CompanyId`).
