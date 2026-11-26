USE StockiaDb;
GO
SET QUOTED_IDENTIFIER ON;
GO
-- SR-10: customers and sales invoices use the tables of db/011 (Partner.Kind = 'Customer', Invoice.Kind = 'Sales').
-- A purchase invoice number is the supplier's, so it is unique per supplier (db/011). A sales invoice number is the
-- company's own numbering, so it is unique in the company, whatever the customer.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Invoice_SalesNumber' AND object_id = OBJECT_ID('dbo.Invoice'))
CREATE UNIQUE INDEX UX_Invoice_SalesNumber ON dbo.Invoice (CompanyId, Number) WHERE Kind = 'Sales';
GO
