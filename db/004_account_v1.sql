USE StockiaDb;
GO
IF OBJECT_ID('dbo.Account') IS NULL
CREATE TABLE dbo.Account (
    Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Account PRIMARY KEY,
    CompanyId INT NOT NULL CONSTRAINT FK_Account_CompanyId REFERENCES dbo.Company(Id),
    ParentId INT NULL CONSTRAINT FK_Account_ParentId REFERENCES dbo.Account(Id),
    Code NVARCHAR(20) NOT NULL,
    Name NVARCHAR(100) NOT NULL,
    Type NVARCHAR(20) NOT NULL,
    IsActive BIT NOT NULL CONSTRAINT DF_Account_IsActive DEFAULT 1,
    CONSTRAINT UQ_Account_Code UNIQUE (CompanyId, Code),
    CONSTRAINT CK_Account_Type CHECK (Type IN ('Asset','Liability','Equity','Revenue','Expense'))
);
GO