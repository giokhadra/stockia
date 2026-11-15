# Definition of Done

An item is done only when all of these are true.

| # | Rule | How it is checked |
|---|------|-------------------|
| d1 | Merged, and the build passes | GitHub Actions (build + tests + CREATE TABLE script on SQL Server) is green on the pull request |
| d2 | Unit tests written first, all pass | Tests are committed before or with the code; `dotnet test` is green |
| d3 | The system test of its SR passes | The matching test in `tests/Stockia.Tests` (e.g. `AuthFlowTests`) passes in CI |
| d4 | The client has seen it work | Demo to Team ARCS; the reviewer ticks the box on the pull request |
| d5 | Every new page requires login and checks the user's company (SR-33) | Login: automatic (fallback policy) and enforced by `ConventionTests`. Company: every query on business data filters by `User.GetCompanyId()`; every new table has `CompanyId` (`ConventionTests`) |
| d6 | Amounts are stored as DECIMAL and totals match to 0.01 (SR-39) | Columns are `DECIMAL(18,2)`; use `Money.Total/Round`; `ConventionTests` and `MoneyTests` |

Pages that must be public need an explicit `[AllowAnonymous]` and a reviewer's OK.
