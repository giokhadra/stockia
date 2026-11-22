USE StockiaDb;
GO
-- SR-7: Partner (suppliers now, customers later), Invoice (purchase now, sales later) and InvoiceLine.
-- Rules (SR-33, SR-39): every business table has CompanyId; every amount is DECIMAL(18,2).
IF OBJECT_ID('dbo.Partner') IS NULL
CREATE TABLE dbo.Partner (
    Id        INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Partner PRIMARY KEY,
    CompanyId INT           NOT NULL CONSTRAINT FK_Partner_CompanyId REFERENCES dbo.Company(Id),
    Kind      NVARCHAR(20)  NOT NULL,
    Name      NVARCHAR(150) NOT NULL,
    TaxNumber NVARCHAR(30)  NULL,
    Phone     NVARCHAR(30)  NULL,
    Balance   DECIMAL(18,2) NOT NULL CONSTRAINT DF_Partner_Balance DEFAULT 0,
    CONSTRAINT UQ_Partner_Name UNIQUE (CompanyId, Name)
);
GO
IF OBJECT_ID('dbo.Invoice') IS NULL
CREATE TABLE dbo.Invoice (
    Id        INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Invoice PRIMARY KEY,
    CompanyId INT           NOT NULL CONSTRAINT FK_Invoice_CompanyId REFERENCES dbo.Company(Id),
    Kind      NVARCHAR(20)  NOT NULL,
    PartnerId INT           NOT NULL CONSTRAINT FK_Invoice_PartnerId REFERENCES dbo.Partner(Id),
    Number    NVARCHAR(50)  NOT NULL,
    Date      DATE          NOT NULL,
    DueDate   DATE          NOT NULL,
    Currency  CHAR(3)       NOT NULL,
    Description NVARCHAR(500) NULL,
    Subtotal  DECIMAL(18,2) NOT NULL,
    VatTotal  DECIMAL(18,2) NOT NULL,
    Total     DECIMAL(18,2) NOT NULL,
    BaseTotal DECIMAL(18,2) NOT NULL,
    CONSTRAINT UQ_Invoice_Number UNIQUE (CompanyId, Kind, PartnerId, Number),
    CONSTRAINT CK_Invoice_Dates CHECK (DueDate >= Date)
);
GO
IF OBJECT_ID('dbo.InvoiceLine') IS NULL
CREATE TABLE dbo.InvoiceLine (
    Id          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_InvoiceLine PRIMARY KEY,
    CompanyId   INT           NOT NULL CONSTRAINT FK_InvoiceLine_CompanyId REFERENCES dbo.Company(Id),
    InvoiceId   INT           NOT NULL CONSTRAINT FK_InvoiceLine_InvoiceId REFERENCES dbo.Invoice(Id) ON DELETE CASCADE,
    ItemId      INT           NOT NULL CONSTRAINT FK_InvoiceLine_ItemId REFERENCES dbo.Item(Id),
    Description NVARCHAR(100) NOT NULL,
    Quantity    DECIMAL(18,2) NOT NULL,
    UnitPrice   DECIMAL(18,2) NOT NULL,
    VatRate     DECIMAL(18,2) NOT NULL,
    IsStock     BIT           NOT NULL,
    CONSTRAINT CK_InvoiceLine_Amounts CHECK (Quantity > 0 AND UnitPrice >= 0 AND VatRate >= 0 AND VatRate <= 100)
);
GO
