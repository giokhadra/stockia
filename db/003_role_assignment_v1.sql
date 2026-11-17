USE StockiaDb;
GO
IF OBJECT_ID('dbo.Role') IS NULL
CREATE TABLE dbo.Role (
    Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Role PRIMARY KEY,
    Name NVARCHAR(30) NOT NULL CONSTRAINT UQ_Role_Name UNIQUE
);
GO
INSERT INTO dbo.Role (Name)
SELECT v.Name FROM (VALUES (N'Administrator'), (N'Chief accountant'), (N'Accountant'), (N'Viewer')) AS v(Name)
WHERE NOT EXISTS (SELECT 1 FROM dbo.Role r WHERE r.Name = v.Name);
GO
IF OBJECT_ID('dbo.Assignment') IS NULL
CREATE TABLE dbo.Assignment (
    UserId INT NOT NULL CONSTRAINT FK_Assignment_UserId REFERENCES dbo.AppUser(Id),
    CompanyId INT NOT NULL CONSTRAINT FK_Assignment_CompanyId REFERENCES dbo.Company(Id),
    RoleId INT NOT NULL CONSTRAINT FK_Assignment_RoleId REFERENCES dbo.Role(Id),
    Since DATE NOT NULL CONSTRAINT DF_Assignment_Since DEFAULT CAST(GETDATE() AS DATE),
    CONSTRAINT PK_Assignment PRIMARY KEY (UserId, CompanyId)
);
GO
-- keep the seeded admin working: administrator of their own company
INSERT INTO dbo.Assignment (UserId, CompanyId, RoleId)
SELECT u.Id, u.CompanyId, r.Id
FROM dbo.AppUser u CROSS JOIN dbo.Role r
WHERE u.Username = 'admin' AND r.Name = N'Administrator'
  AND NOT EXISTS (SELECT 1 FROM dbo.Assignment a WHERE a.UserId = u.Id AND a.CompanyId = u.CompanyId);
GO