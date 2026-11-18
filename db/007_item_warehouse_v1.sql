USE StockiaDb;
GO
-- SR-13: Item and Warehouse. Rules (SR-33, SR-39): every business table has CompanyId; every amount is DECIMAL(18,2).
IF OBJECT_ID('dbo.Item') IS NULL
CREATE TABLE dbo.Item (
    Id        INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Item PRIMARY KEY,
    CompanyId INT           NOT NULL CONSTRAINT FK_Item_CompanyId REFERENCES dbo.Company(Id),
    Code      NVARCHAR(20)  NOT NULL,
    Name      NVARCHAR(100) NOT NULL,
    Unit      NVARCHAR(10)  NOT NULL,
    Category  NVARCHAR(50)  NULL,
    Cost      DECIMAL(18,2) NOT NULL CONSTRAINT DF_Item_Cost DEFAULT 0,
    SalePrice DECIMAL(18,2) NOT NULL,
    VatRate   DECIMAL(18,2) NOT NULL,
    IsStock   BIT           NOT NULL CONSTRAINT DF_Item_IsStock DEFAULT 1,
    CONSTRAINT UQ_Item_Code UNIQUE (CompanyId, Code),
    CONSTRAINT CK_Item_Amounts CHECK (Cost >= 0 AND SalePrice >= 0 AND VatRate >= 0 AND VatRate <= 100)
);
GO
IF OBJECT_ID('dbo.Warehouse') IS NULL
CREATE TABLE dbo.Warehouse (
    Id        INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Warehouse PRIMARY KEY,
    CompanyId INT           NOT NULL CONSTRAINT FK_Warehouse_CompanyId REFERENCES dbo.Company(Id),
    Name      NVARCHAR(100) NOT NULL,
    CONSTRAINT UQ_Warehouse_Name UNIQUE (CompanyId, Name)
);
GO