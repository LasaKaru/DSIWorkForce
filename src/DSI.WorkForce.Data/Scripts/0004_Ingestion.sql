-- Durable work queue: one row per scanned PDF. Workers claim rows with READPAST so several
-- worker instances can run in parallel without processing the same file twice.
CREATE TABLE dsi.ScanFile
(
    ScanFileId    BIGINT         IDENTITY(1,1) NOT NULL CONSTRAINT PK_ScanFile PRIMARY KEY,
    FileName      NVARCHAR(260)  NOT NULL,
    StoredPath    NVARCHAR(500)  NOT NULL,
    Sha256        CHAR(64)       NOT NULL,
    TemplateCode  NVARCHAR(30)   NOT NULL,
    SourceStation NVARCHAR(100)  NULL,
    UploadedBy    NVARCHAR(100)  NULL,
    PageCount     INT            NULL,
    Status        NVARCHAR(20)   NOT NULL CONSTRAINT DF_ScanFile_Status DEFAULT (N'Pending'),
    Attempts      INT            NOT NULL CONSTRAINT DF_ScanFile_Attempts DEFAULT (0),
    NextAttemptAt DATETIME2(3)   NOT NULL CONSTRAINT DF_ScanFile_NextAttemptAt DEFAULT (SYSUTCDATETIME()),
    LockedBy      NVARCHAR(100)  NULL,
    LockedUntil   DATETIME2(3)   NULL,
    LastError     NVARCHAR(2000) NULL,
    ReceivedAt    DATETIME2(3)   NOT NULL CONSTRAINT DF_ScanFile_ReceivedAt DEFAULT (SYSUTCDATETIME()),
    CompletedAt   DATETIME2(3)   NULL,
    CONSTRAINT UQ_ScanFile_Sha256 UNIQUE (Sha256),   -- same PDF can never be imported twice
    CONSTRAINT CK_ScanFile_Status CHECK (Status IN (N'Pending', N'Processing', N'Completed', N'Failed', N'Duplicate'))
);
GO
CREATE INDEX IX_ScanFile_Queue ON dsi.ScanFile (Status, NextAttemptAt) INCLUDE (LockedUntil, Attempts);
GO
CREATE TABLE dsi.WorkerHeartbeat
(
    WorkerName     NVARCHAR(100) NOT NULL CONSTRAINT PK_WorkerHeartbeat PRIMARY KEY,
    LastSeenUtc    DATETIME2(3)  NOT NULL,
    Version        NVARCHAR(50)  NULL,
    ProcessedPages BIGINT        NOT NULL CONSTRAINT DF_WorkerHeartbeat_Processed DEFAULT (0)
);
GO
