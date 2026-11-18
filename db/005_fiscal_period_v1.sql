USE StockiaDb;
GO
-- SR-18 / UC-14: every company has its own monthly fiscal periods (closed later by SR-5).
IF OBJECT_ID('dbo.FiscalPeriod') IS NULL
CREATE TABLE dbo.FiscalPeriod (
    Id        INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_FiscalPeriod PRIMARY KEY,
    CompanyId INT          NOT NULL CONSTRAINT FK_FiscalPeriod_CompanyId REFERENCES dbo.Company(Id),
    StartDate DATE         NOT NULL,
    EndDate   DATE         NOT NULL,
    Status    NVARCHAR(10) NOT NULL CONSTRAINT DF_FiscalPeriod_Status DEFAULT N'Open',
    CONSTRAINT UQ_FiscalPeriod_Start UNIQUE (CompanyId, StartDate),
    CONSTRAINT CK_FiscalPeriod_Dates CHECK (EndDate >= StartDate),
    CONSTRAINT CK_FiscalPeriod_Status CHECK (Status IN (N'Open', N'Closed'))
);
GO
-- Companies created before this script get the twelve months of the current year.
INSERT INTO dbo.FiscalPeriod (CompanyId, StartDate, EndDate)
SELECT c.Id, m.StartDate, EOMONTH(m.StartDate)
FROM dbo.Company c
CROSS JOIN (SELECT DATEFROMPARTS(YEAR(GETDATE()), v.n, 1) AS StartDate
            FROM (VALUES (1),(2),(3),(4),(5),(6),(7),(8),(9),(10),(11),(12)) AS v(n)) AS m
WHERE NOT EXISTS (SELECT 1 FROM dbo.FiscalPeriod p WHERE p.CompanyId = c.Id);
GO
