-- Master data used to validate and auto-correct OCR results.
CREATE TABLE dsi.Employee
(
    EpfNo       NVARCHAR(20)  NOT NULL CONSTRAINT PK_Employee PRIMARY KEY,
    Name        NVARCHAR(150) NOT NULL,
    Section     NVARCHAR(50)  NULL,
    IsActive    BIT           NOT NULL CONSTRAINT DF_Employee_IsActive DEFAULT (1),
    UpdatedAt   DATETIME2(3)  NOT NULL CONSTRAINT DF_Employee_UpdatedAt DEFAULT (SYSUTCDATETIME())
);
GO
CREATE TABLE dsi.Machine
(
    ResourceNo   NVARCHAR(20)  NOT NULL CONSTRAINT PK_Machine PRIMARY KEY,   -- e.g. P 24
    Name         NVARCHAR(100) NULL,
    Section      NVARCHAR(50)  NULL,
    UnitsPerTick DECIMAL(9,2)  NULL,   -- tyres represented by one tick on the hourly grid (cavities). NULL = template default
    IsActive     BIT           NOT NULL CONSTRAINT DF_Machine_IsActive DEFAULT (1),
    UpdatedAt    DATETIME2(3)  NOT NULL CONSTRAINT DF_Machine_UpdatedAt DEFAULT (SYSUTCDATETIME())
);
GO
CREATE TABLE dsi.Item
(
    ItemCode            NVARCHAR(50)  NOT NULL CONSTRAINT PK_Item PRIMARY KEY,
    Description         NVARCHAR(200) NOT NULL,                 -- e.g. 29 2.10 SRI 94 Full Black
    Category            NVARCHAR(50)  NULL,
    StandardRatePerHour DECIMAL(10,2) NULL,
    IsActive            BIT           NOT NULL CONSTRAINT DF_Item_IsActive DEFAULT (1),
    UpdatedAt           DATETIME2(3)  NOT NULL CONSTRAINT DF_Item_UpdatedAt DEFAULT (SYSUTCDATETIME())
);
GO
-- Learned spellings: when a reviewer fixes "29x2-10 SDG 94 F/B" to an item, the canonical spelling is stored here
-- and future sheets with the same handwriting match automatically.
CREATE TABLE dsi.ItemAlias
(
    Alias     NVARCHAR(200) NOT NULL CONSTRAINT PK_ItemAlias PRIMARY KEY,
    ItemCode  NVARCHAR(50)  NOT NULL CONSTRAINT FK_ItemAlias_Item REFERENCES dsi.Item (ItemCode),
    CreatedBy NVARCHAR(100) NULL,
    CreatedAt DATETIME2(3)  NOT NULL CONSTRAINT DF_ItemAlias_CreatedAt DEFAULT (SYSUTCDATETIME())
);
GO
CREATE TABLE dsi.ShiftMaster
(
    ShiftCode NVARCHAR(10) NOT NULL CONSTRAINT PK_ShiftMaster PRIMARY KEY,   -- as written on the sheet: 6-2, 2-10, 10-6
    StartTime TIME(0)      NOT NULL,
    EndTime   TIME(0)      NOT NULL,
    Hours     DECIMAL(4,2) NOT NULL
);
GO
INSERT dsi.ShiftMaster (ShiftCode, StartTime, EndTime, Hours) VALUES
 (N'6-2',  '06:00', '14:00', 8),
 (N'2-10', '14:00', '22:00', 8),
 (N'10-6', '22:00', '06:00', 8);
GO
