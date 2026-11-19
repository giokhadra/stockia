USE StockiaDb;
GO
SET QUOTED_IDENTIFIER ON;
GO
-- SR-4: an entry posted automatically is linked to the document it comes from (sales invoice, purchase invoice,
-- payment, receipt). SourceType + SourceId are both NULL on a manual entry.
-- Rules (SR-33): JournalEntry already has CompanyId. No amount is added here (SR-39).
IF COL_LENGTH('dbo.JournalEntry', 'SourceType') IS NULL
ALTER TABLE dbo.JournalEntry ADD SourceType NVARCHAR(30) NULL;
GO
IF COL_LENGTH('dbo.JournalEntry', 'SourceId') IS NULL
ALTER TABLE dbo.JournalEntry ADD SourceId INT NULL;
GO
-- Both or neither.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_JournalEntry_Source' AND parent_object_id = OBJECT_ID('dbo.JournalEntry'))
ALTER TABLE dbo.JournalEntry ADD CONSTRAINT CK_JournalEntry_Source
    CHECK ((SourceType IS NULL AND SourceId IS NULL) OR (SourceType IS NOT NULL AND SourceId IS NOT NULL));
GO
-- A document is posted only once: one entry per company, document type and document id.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_JournalEntry_Source' AND object_id = OBJECT_ID('dbo.JournalEntry'))
CREATE UNIQUE INDEX UX_JournalEntry_Source ON dbo.JournalEntry (CompanyId, SourceType, SourceId) WHERE SourceType IS NOT NULL;
GO
