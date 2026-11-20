-- Report metadata. Each report becomes a generated view rpt.vw_<Code> that looks like the Excel sheet.
-- To add an Excel column: insert a row in rpt.ReportColumn (or use Admin > Report Columns) and rebuild the view.
CREATE TABLE rpt.ReportDefinition
(
    ReportId      INT            IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReportDefinition PRIMARY KEY,
    Code          NVARCHAR(64)   NOT NULL CONSTRAINT UQ_ReportDefinition_Code UNIQUE,
    Title         NVARCHAR(200)  NOT NULL,
    Subtitle      NVARCHAR(200)  NULL,
    TemplateCodes NVARCHAR(200)  NULL,      -- comma separated; NULL = all templates
    OnlyApproved  BIT            NOT NULL CONSTRAINT DF_ReportDefinition_OnlyApproved DEFAULT (0),
    UpdatedAt     DATETIME2(3)   NOT NULL CONSTRAINT DF_ReportDefinition_UpdatedAt DEFAULT (SYSUTCDATETIME()),
    LastBuiltAt   DATETIME2(3)   NULL,
    LastBuiltSql  NVARCHAR(MAX)  NULL
);
GO
CREATE TABLE rpt.ReportColumn
(
    ReportColumnId INT            IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReportColumn PRIMARY KEY,
    ReportId       INT            NOT NULL CONSTRAINT FK_ReportColumn_Report REFERENCES rpt.ReportDefinition (ReportId) ON DELETE CASCADE,
    ColumnOrder    INT            NOT NULL,
    ColumnKey      NVARCHAR(64)   NOT NULL,
    Header         NVARCHAR(128)  NOT NULL,
    SourceType     NVARCHAR(20)   NOT NULL CONSTRAINT CK_ReportColumn_SourceType CHECK (SourceType IN (N'Header', N'Field', N'FieldAggregate', N'Lookup', N'Expression')),
    Source         NVARCHAR(100)  NULL,
    RowIndex       INT            NULL,
    Aggregate      NVARCHAR(10)   NULL,
    Expression     NVARCHAR(1000) NULL,
    Format         NVARCHAR(50)   NULL,
    IsVisible      BIT            NOT NULL CONSTRAINT DF_ReportColumn_IsVisible DEFAULT (1),
    CONSTRAINT UQ_ReportColumn_Key UNIQUE (ReportId, ColumnKey)
);
GO
