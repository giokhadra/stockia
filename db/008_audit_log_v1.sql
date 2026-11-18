USE StockiaDb;
GO
SET QUOTED_IDENTIFIER ON;
GO
-- SR-6: AuditLog (who did what to an entry, and when) and the link from a reversal to the entry it reverses.
-- Rules (SR-33): every business table has CompanyId. Entries are never deleted: they are reversed.
IF OBJECT_ID('dbo.AuditLog') IS NULL
CREATE TABLE dbo.AuditLog (
    Id        INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuditLog PRIMARY KEY,
    CompanyId INT           NOT NULL CONSTRAINT FK_AuditLog_CompanyId REFERENCES dbo.Company(Id),
    UserId    INT           NOT NULL CONSTRAINT FK_AuditLog_UserId REFERENCES dbo.AppUser(Id),
    EntryId   INT           NULL     CONSTRAINT FK_AuditLog_EntryId REFERENCES dbo.JournalEntry(Id),
    LogTime   DATETIME2     NOT NULL CONSTRAINT DF_AuditLog_LogTime DEFAULT SYSUTCDATETIME(),
    Action    NVARCHAR(50)  NOT NULL,
    Details   NVARCHAR(400) NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AuditLog_CompanyId_LogTime' AND object_id = OBJECT_ID('dbo.AuditLog'))
CREATE INDEX IX_AuditLog_CompanyId_LogTime ON dbo.AuditLog (CompanyId, LogTime);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AuditLog_EntryId' AND object_id = OBJECT_ID('dbo.AuditLog'))
CREATE INDEX IX_AuditLog_EntryId ON dbo.AuditLog (EntryId);
GO
-- A reversal points to the entry it reverses.
IF COL_LENGTH('dbo.JournalEntry', 'ReversesEntryId') IS NULL
ALTER TABLE dbo.JournalEntry ADD ReversesEntryId INT NULL
    CONSTRAINT FK_JournalEntry_ReversesEntryId REFERENCES dbo.JournalEntry(Id);
GO
-- An entry can be reversed only once.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_JournalEntry_ReversesEntryId' AND object_id = OBJECT_ID('dbo.JournalEntry'))
CREATE UNIQUE INDEX UX_JournalEntry_ReversesEntryId ON dbo.JournalEntry (ReversesEntryId) WHERE ReversesEntryId IS NOT NULL;
GO