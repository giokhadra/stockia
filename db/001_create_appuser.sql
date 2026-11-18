-- Idempotent: safe to run repeatedly (locally and in GitHub Actions).
-- Local dev note: if you created AppUser from the first version of this script
-- (no CompanyId), drop the StockiaDb database and run this script again.
IF DB_ID('StockiaDb') IS NULL CREATE DATABASE StockiaDb;
GO
USE StockiaDb;
GO
IF OBJECT_ID('dbo.Company') IS NULL
CREATE TABLE dbo.Company (
    Id   INT IDENTITY(1,1) PRIMARY KEY,
    Name NVARCHAR(200) NOT NULL
);
GO
IF OBJECT_ID('dbo.AppUser') IS NULL
CREATE TABLE dbo.AppUser (
    Id           INT IDENTITY(1,1) PRIMARY KEY,
    CompanyId    INT           NOT NULL,
    Username     NVARCHAR(100) NOT NULL,
    PasswordHash NVARCHAR(400) NOT NULL,
    DisplayName  NVARCHAR(150) NOT NULL,
    CreatedAt    DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_AppUser_Username UNIQUE (Username),
    CONSTRAINT FK_AppUser_Company FOREIGN KEY (CompanyId) REFERENCES dbo.Company(Id)
);
GO
-- Rule for later tables (SR-33, SR-39): every business table carries CompanyId,
-- and every money amount is DECIMAL(18,2), never FLOAT/REAL/MONEY.
