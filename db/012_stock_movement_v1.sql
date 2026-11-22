USE StockiaDb;
GO
SET QUOTED_IDENTIFIER ON;
GO
-- SR-14 / UC-3: a purchase of stock items raises stock. Stock is not stored as a number: it is the sum of the
-- movements (Design 4.2). Each movement keeps its quantity and its value in the base currency, DECIMAL(18,2) (SR-39).
-- CompanyId on the table (SR-33). A purchase invoice says which warehouse receives its stock lines.
IF COL_LENGTH('dbo.Invoice', 'WarehouseId') IS NULL
    ALTER TABLE dbo.Invoice ADD WarehouseId INT NULL CONSTRAINT FK_Invoice_WarehouseId REFERENCES dbo.Warehouse(Id);
GO
IF OBJECT_ID('dbo.StockMovement') IS NULL
CREATE TABLE dbo.StockMovement (
    Id            INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_StockMovement PRIMARY KEY,
    CompanyId     INT           NOT NULL CONSTRAINT FK_StockMovement_CompanyId REFERENCES dbo.Company(Id),
    ItemId        INT           NOT NULL CONSTRAINT FK_StockMovement_ItemId REFERENCES dbo.Item(Id),
    WarehouseId   INT           NOT NULL CONSTRAINT FK_StockMovement_WarehouseId REFERENCES dbo.Warehouse(Id),
    InvoiceLineId INT           NULL     CONSTRAINT FK_StockMovement_InvoiceLineId REFERENCES dbo.InvoiceLine(Id),
    Date          DATE          NOT NULL,
    Type          NVARCHAR(10)  NOT NULL,
    Quantity      DECIMAL(18,2) NOT NULL,
    Value         DECIMAL(18,2) NOT NULL,
    CONSTRAINT CK_StockMovement_Type CHECK (Type IN ('In', 'Out', 'Adjust'))
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_StockMovement_CompanyId_ItemId_WarehouseId')
    CREATE INDEX IX_StockMovement_CompanyId_ItemId_WarehouseId ON dbo.StockMovement (CompanyId, ItemId, WarehouseId);
GO
-- one movement per invoice line: a line is never received twice
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_StockMovement_InvoiceLineId')
    CREATE UNIQUE INDEX UX_StockMovement_InvoiceLineId ON dbo.StockMovement (InvoiceLineId) WHERE InvoiceLineId IS NOT NULL;
GO