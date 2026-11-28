USE StockiaDb;
GO
SET QUOTED_IDENTIFIER ON;
GO
-- SR-11 (customer receipts) and SR-8 (supplier payments): money that settles invoices, in one table like Invoice
-- (Kind = 'Receipt' or 'Payment'), and the allocation of each to the invoices it pays.
-- Rules: CompanyId on both tables (SR-33); amounts DECIMAL(18,2) (SR-39); Method is where the money went (531 / 512).
IF OBJECT_ID('dbo.Settlement') IS NULL
CREATE TABLE dbo.Settlement (
    Id          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Settlement PRIMARY KEY,
    CompanyId   INT           NOT NULL CONSTRAINT FK_Settlement_CompanyId REFERENCES dbo.Company(Id),
    Kind        NVARCHAR(20)  NOT NULL CONSTRAINT CK_Settlement_Kind CHECK (Kind IN ('Receipt', 'Payment')),
    PartnerId   INT           NOT NULL CONSTRAINT FK_Settlement_PartnerId REFERENCES dbo.Partner(Id),
    Number      NVARCHAR(50)  NOT NULL,
    [Date]      DATE          NOT NULL,
    Method      NVARCHAR(10)  NOT NULL CONSTRAINT CK_Settlement_Method CHECK (Method IN ('Cash', 'Bank')),
    Currency    CHAR(3)       NOT NULL,
    Amount      DECIMAL(18,2) NOT NULL CONSTRAINT CK_Settlement_Amount CHECK (Amount > 0),
    BaseAmount  DECIMAL(18,2) NOT NULL CONSTRAINT CK_Settlement_BaseAmount CHECK (BaseAmount >= 0),
    Description NVARCHAR(500) NULL,
    CONSTRAINT UQ_Settlement_Number UNIQUE (CompanyId, Kind, Number)
);
GO
IF OBJECT_ID('dbo.SettlementAllocation') IS NULL
CREATE TABLE dbo.SettlementAllocation (
    Id           INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SettlementAllocation PRIMARY KEY,
    CompanyId    INT           NOT NULL CONSTRAINT FK_SettlementAllocation_CompanyId REFERENCES dbo.Company(Id),
    SettlementId INT           NOT NULL CONSTRAINT FK_SettlementAllocation_SettlementId REFERENCES dbo.Settlement(Id) ON DELETE CASCADE,
    InvoiceId    INT           NOT NULL CONSTRAINT FK_SettlementAllocation_InvoiceId REFERENCES dbo.Invoice(Id),
    Amount       DECIMAL(18,2) NOT NULL CONSTRAINT CK_SettlementAllocation_Amount CHECK (Amount > 0),
    BaseAmount   DECIMAL(18,2) NOT NULL CONSTRAINT CK_SettlementAllocation_BaseAmount CHECK (BaseAmount >= 0)
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SettlementAllocation_InvoiceId')
CREATE INDEX IX_SettlementAllocation_InvoiceId ON dbo.SettlementAllocation (InvoiceId);
GO
