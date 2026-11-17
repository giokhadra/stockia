USE StockiaDb;
GO
IF OBJECT_ID('dbo.Currency') IS NULL
CREATE TABLE dbo.Currency (
    Code CHAR(3) NOT NULL PRIMARY KEY,
    Name NVARCHAR(50) NOT NULL
);
GO
INSERT INTO dbo.Currency (Code, Name)
SELECT v.Code, v.Name
FROM (VALUES ('LBP', N'Lebanese pound'), ('USD', N'US dollar'), ('EUR', N'Euro')) AS v(Code, Name)
WHERE NOT EXISTS (SELECT 1 FROM dbo.Currency c WHERE c.Code = v.Code);
GO
IF COL_LENGTH('dbo.Company', 'TaxNumber') IS NULL
    ALTER TABLE dbo.Company ADD TaxNumber NVARCHAR(30) NULL;
GO
IF COL_LENGTH('dbo.Company', 'BaseCurrency') IS NULL
    ALTER TABLE dbo.Company ADD BaseCurrency CHAR(3) NOT NULL
        CONSTRAINT DF_Company_BaseCurrency DEFAULT 'USD'
        CONSTRAINT FK_Company_BaseCurrency REFERENCES dbo.Currency(Code);
GO