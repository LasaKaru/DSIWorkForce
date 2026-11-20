-- Form templates (paper layouts) and the global field dictionary.
CREATE TABLE dsi.FormTemplate
(
    TemplateId     INT            IDENTITY(1,1) NOT NULL CONSTRAINT PK_FormTemplate PRIMARY KEY,
    TemplateCode   NVARCHAR(30)   NOT NULL,      -- PC-011-A
    Version        INT            NOT NULL,
    Name           NVARCHAR(200)  NOT NULL,
    DefinitionJson NVARCHAR(MAX)  NOT NULL,      -- zones, extractors, validation rules (see templates/forms/*.json)
    IsActive       BIT            NOT NULL CONSTRAINT DF_FormTemplate_IsActive DEFAULT (1),
    CreatedAt      DATETIME2(3)   NOT NULL CONSTRAINT DF_FormTemplate_CreatedAt DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT UQ_FormTemplate_Code_Version UNIQUE (TemplateCode, Version),
    CONSTRAINT CK_FormTemplate_Json CHECK (ISJSON(DefinitionJson) = 1)
);
GO
-- One row per logical field (EPF_NO, HOURLY_UNITS ...). Shared by all template versions so a new
-- print of the form keeps feeding the same report columns.
CREATE TABLE dsi.FieldDefinition
(
    FieldId   INT           IDENTITY(1,1) NOT NULL CONSTRAINT PK_FieldDefinition PRIMARY KEY,
    FieldKey  NVARCHAR(64)  NOT NULL CONSTRAINT UQ_FieldDefinition_Key UNIQUE,
    Label     NVARCHAR(200) NOT NULL,
    DataType  NVARCHAR(20)  NOT NULL CONSTRAINT CK_FieldDefinition_DataType CHECK (DataType IN (N'Text', N'Integer', N'Decimal', N'Date', N'Time', N'Checkbox')),
    GroupName NVARCHAR(100) NULL,
    IsActive  BIT           NOT NULL CONSTRAINT DF_FieldDefinition_IsActive DEFAULT (1),
    CreatedAt DATETIME2(3)  NOT NULL CONSTRAINT DF_FieldDefinition_CreatedAt DEFAULT (SYSUTCDATETIME())
);
GO
