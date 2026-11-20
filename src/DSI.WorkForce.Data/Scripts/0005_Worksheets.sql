-- One row per scanned page (= one paper worksheet). Frequently-filtered values are "promoted"
-- into indexed columns; every captured value lives in dsi.WorksheetValue (narrow, typed, extensible).
CREATE TABLE dsi.Worksheet
(
    WorksheetId     BIGINT        IDENTITY(1,1) NOT NULL CONSTRAINT PK_Worksheet PRIMARY KEY,
    ScanFileId      BIGINT        NOT NULL CONSTRAINT FK_Worksheet_ScanFile REFERENCES dsi.ScanFile (ScanFileId),
    PageNo          INT           NOT NULL,
    TemplateCode    NVARCHAR(30)  NOT NULL,
    TemplateVersion INT           NOT NULL,
    WorkDate        DATE          NULL,
    Shift           NVARCHAR(10)  NULL,
    ResourceNo      NVARCHAR(20)  NULL,
    EpfNo           NVARCHAR(20)  NULL,
    ItemCode        NVARCHAR(50)  NULL,
    Status          NVARCHAR(20)  NOT NULL,
    MinConfidence   DECIMAL(5,4)  NOT NULL,
    ImagePath       NVARCHAR(500) NULL,
    CreatedAt       DATETIME2(3)  NOT NULL CONSTRAINT DF_Worksheet_CreatedAt DEFAULT (SYSUTCDATETIME()),
    VerifiedBy      NVARCHAR(100) NULL,
    VerifiedAt      DATETIME2(3)  NULL,
    RowVer          ROWVERSION    NOT NULL,   -- optimistic concurrency for two reviewers on one sheet
    CONSTRAINT UQ_Worksheet_File_Page UNIQUE (ScanFileId, PageNo),   -- makes re-processing idempotent
    CONSTRAINT CK_Worksheet_Status CHECK (Status IN (N'NeedsReview', N'AutoApproved', N'Verified', N'Rejected'))
);
GO
CREATE INDEX IX_Worksheet_WorkDate ON dsi.Worksheet (WorkDate, Shift, ResourceNo) INCLUDE (EpfNo, ItemCode, Status);
CREATE INDEX IX_Worksheet_Epf ON dsi.Worksheet (EpfNo, WorkDate);
CREATE INDEX IX_Worksheet_Review ON dsi.Worksheet (CreatedAt) INCLUDE (Status) WHERE Status = N'NeedsReview';
GO
CREATE TABLE dsi.WorksheetValue
(
    WorksheetId BIGINT        NOT NULL CONSTRAINT FK_WorksheetValue_Worksheet REFERENCES dsi.Worksheet (WorksheetId) ON DELETE CASCADE,
    FieldId     INT           NOT NULL CONSTRAINT FK_WorksheetValue_Field REFERENCES dsi.FieldDefinition (FieldId),
    RowIndex    TINYINT       NOT NULL,   -- 0 = single value, 1..n = table row (hour slot, mould log line)
    RawText     NVARCHAR(400) NULL,       -- exactly what OCR/OMR produced
    ValueText   NVARCHAR(400) NULL,
    ValueNumber DECIMAL(18,4) NULL,
    ValueDate   DATE          NULL,
    ValueBool   BIT           NULL,
    Confidence  DECIMAL(5,4)  NOT NULL,
    Source      NVARCHAR(10)  NOT NULL,   -- Omr, Ocr, Lookup, Manual, Computed
    IsCorrected BIT           NOT NULL CONSTRAINT DF_WorksheetValue_IsCorrected DEFAULT (0),
    CONSTRAINT PK_WorksheetValue PRIMARY KEY CLUSTERED (WorksheetId, FieldId, RowIndex) WITH (DATA_COMPRESSION = PAGE)
);
GO
CREATE TABLE dsi.ValidationIssue
(
    IssueId     BIGINT         IDENTITY(1,1) NOT NULL CONSTRAINT PK_ValidationIssue PRIMARY KEY,
    WorksheetId BIGINT         NOT NULL CONSTRAINT FK_ValidationIssue_Worksheet REFERENCES dsi.Worksheet (WorksheetId) ON DELETE CASCADE,
    FieldKey    NVARCHAR(64)   NULL,
    RowIndex    TINYINT        NOT NULL,
    Severity    NVARCHAR(10)   NOT NULL,
    Code        NVARCHAR(50)   NOT NULL,
    Message     NVARCHAR(500)  NOT NULL,
    IsResolved  BIT            NOT NULL CONSTRAINT DF_ValidationIssue_IsResolved DEFAULT (0),
    ResolvedBy  NVARCHAR(100)  NULL,
    ResolvedAt  DATETIME2(3)   NULL
);
GO
CREATE INDEX IX_ValidationIssue_Worksheet ON dsi.ValidationIssue (WorksheetId) INCLUDE (IsResolved, Severity);
GO
-- Every manual correction is audited (who changed what, from what, when).
CREATE TABLE dsi.ValueAudit
(
    AuditId     BIGINT        IDENTITY(1,1) NOT NULL CONSTRAINT PK_ValueAudit PRIMARY KEY,
    WorksheetId BIGINT        NOT NULL,
    FieldId     INT           NOT NULL,
    RowIndex    TINYINT       NOT NULL,
    OldValue    NVARCHAR(400) NULL,
    NewValue    NVARCHAR(400) NULL,
    ChangedBy   NVARCHAR(100) NOT NULL,
    ChangedAt   DATETIME2(3)  NOT NULL CONSTRAINT DF_ValueAudit_ChangedAt DEFAULT (SYSUTCDATETIME())
);
GO
CREATE INDEX IX_ValueAudit_Worksheet ON dsi.ValueAudit (WorksheetId);
GO
