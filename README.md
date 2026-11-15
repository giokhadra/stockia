# Stockia (ERP accounting) - walking skeleton

## Setup
1. Install the .NET 8 SDK and SQL Server (Express/LocalDB/Developer) plus SSMS or `sqlcmd`.
2. Clone the repository.
3. Create the database and table: run `db/001_create_appuser.sql` against your SQL Server.
4. Check `ConnectionStrings:StockiaDb` in `src/Stockia.Web/appsettings.json`
   (adjust `Server=` for your machine).
5. Build and test: `dotnet build` then `dotnet test`
   (tests use an in-memory database, so they do not need SQL Server).
6. Run: `dotnet run --project src/Stockia.Web` and open the URL printed in the console.
7. Seeded development login: `admin` / `Admin#2026` (created on first start if AppUser is empty).

## Palette
`#D4AF37` gold, `#FFFFFF` white, `#1A1A1A` charcoal (CSS variables in `src/Stockia.Web/wwwroot/css/site.css`).

## Quality rules
See `docs/DEFINITION_OF_DONE.md`. CI runs the CREATE TABLE script on a SQL Server container and then the tests.
If you created `AppUser` with the first version of the script, drop `StockiaDb` and re-run `db/001_create_appuser.sql`
(the script now also creates `Company` and `AppUser.CompanyId`).
