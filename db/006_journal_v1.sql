USE StockiaDb;
GO
-- SR-2: JournalEntry and JournalLine. The Account table already exists (004_account_v1.sql).
-- Rules (SR-33, SR-39): every business table has CompanyId; every amount is DECIMAL(18,2).
IF OBJECT_ID('dbo.JournalEntry') IS NULL
CREATE TABLE dbo.JournalEntry (
    Id          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_JournalEntry PRIMARY KEY,
    CompanyId   INT           NOT NULL CONSTRAINT FK_JournalEntry_CompanyId REFERENCES dbo.Company(Id),
    [Date]      DATE          NOT NULL,
    Reference   NVARCHAR(50)  NOT NULL,
    Description NVARCHAR(500) NOT NULL CONSTRAINT DF_JournalEntry_Description DEFAULT N'',
    CreatedAt   DATETIME2     NOT NULL CONSTRAINT DF_JournalEntry_CreatedAt DEFAULT SYSUTCDATETIME()
);
GO
IF OBJECT_ID('dbo.JournalLine') IS NULL
CREATE TABLE dbo.JournalLine (
    Id             INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_JournalLine PRIMARY KEY,
    CompanyId      INT           NOT NULL CONSTRAINT FK_JournalLine_CompanyId REFERENCES dbo.Company(Id),
    JournalEntryId INT           NOT NULL CONSTRAINT FK_JournalLine_JournalEntryId REFERENCES dbo.JournalEntry(Id) ON DELETE CASCADE,
    AccountId      INT           NOT NULL CONSTRAINT FK_JournalLine_AccountId REFERENCES dbo.Account(Id),
    Debit          DECIMAL(18,2) NOT NULL,
    Credit         DECIMAL(18,2) NOT NULL,
    CONSTRAINT CK_JournalLine_OneSide CHECK (Debit >= 0 AND Credit >= 0 AND ((Debit > 0 AND Credit = 0) OR (Debit = 0 AND Credit > 0)))
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_JournalLine_AccountId' AND object_id = OBJECT_ID('dbo.JournalLine'))
CREATE INDEX IX_JournalLine_AccountId ON dbo.JournalLine (AccountId);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_JournalLine_JournalEntryId' AND object_id = OBJECT_ID('dbo.JournalLine'))
CREATE INDEX IX_JournalLine_JournalEntryId ON dbo.JournalLine (JournalEntryId);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_JournalEntry_CompanyId' AND object_id = OBJECT_ID('dbo.JournalEntry'))
CREATE INDEX IX_JournalEntry_CompanyId ON dbo.JournalEntry (CompanyId);
GO
