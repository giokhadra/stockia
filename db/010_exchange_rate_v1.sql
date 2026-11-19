USE StockiaDb;
GO
SET QUOTED_IDENTIFIER ON;
GO
-- SR-19: exchange rates of a company, and the currency, foreign amount and rate of a journal line.
-- Rules (SR-33, SR-39): CompanyId on the new table; amounts stay DECIMAL(18,2), the base amount stays in Debit/Credit;
-- a rate is DECIMAL(20,10) so that a small rate (1 LBP in USD) still converts exactly to the cent.
IF OBJECT_ID('dbo.ExchangeRate') IS NULL
CREATE TABLE dbo.ExchangeRate (
    Id        INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ExchangeRate PRIMARY KEY,
    CompanyId INT            NOT NULL CONSTRAINT FK_ExchangeRate_CompanyId REFERENCES dbo.Company(Id),
    Currency  CHAR(3)        NOT NULL,
    [Date]    DATE           NOT NULL,
    Rate      DECIMAL(20,10) NOT NULL CONSTRAINT CK_ExchangeRate_Rate CHECK (Rate > 0),
    CONSTRAINT UX_ExchangeRate_Company_Currency_Date UNIQUE (CompanyId, Currency, [Date])
);
GO
IF COL_LENGTH('dbo.JournalLine', 'Currency') IS NULL
ALTER TABLE dbo.JournalLine ADD Currency CHAR(3) NULL;
GO
IF COL_LENGTH('dbo.JournalLine', 'ForeignAmount') IS NULL
ALTER TABLE dbo.JournalLine ADD ForeignAmount DECIMAL(18,2) NULL;
GO
IF COL_LENGTH('dbo.JournalLine', 'Rate') IS NULL
ALTER TABLE dbo.JournalLine ADD Rate DECIMAL(20,10) NULL;
GO
-- All three or none: a base-currency line has none of them.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_JournalLine_Foreign' AND parent_object_id = OBJECT_ID('dbo.JournalLine'))
ALTER TABLE dbo.JournalLine ADD CONSTRAINT CK_JournalLine_Foreign CHECK (
    (Currency IS NULL AND ForeignAmount IS NULL AND Rate IS NULL)
    OR (Currency IS NOT NULL AND ForeignAmount > 0 AND Rate > 0));
GO
