USE StockiaDb;
GO
SET QUOTED_IDENTIFIER ON;
GO
-- Item 17, SR-15 / UC-9: a sale beyond the stock of its warehouse is kept but not posted; it waits until a chief
-- accountant or an administrator approves it (then it is posted and the stock falls) or rejects it.
-- Every invoice saved before this script is posted, so the existing rows take 'Posted'.
IF COL_LENGTH('dbo.Invoice', 'Status') IS NULL
    ALTER TABLE dbo.Invoice ADD Status NVARCHAR(20) NOT NULL
        CONSTRAINT DF_Invoice_Status DEFAULT 'Posted'
        CONSTRAINT CK_Invoice_Status CHECK (Status IN ('Posted', 'PendingApproval', 'Rejected'));
GO
IF COL_LENGTH('dbo.Invoice', 'DecidedByUserId') IS NULL
    ALTER TABLE dbo.Invoice ADD DecidedByUserId INT NULL CONSTRAINT FK_Invoice_DecidedByUserId REFERENCES dbo.AppUser(Id);
GO
IF COL_LENGTH('dbo.Invoice', 'DecidedAt') IS NULL
    ALTER TABLE dbo.Invoice ADD DecidedAt DATETIME2 NULL;
GO