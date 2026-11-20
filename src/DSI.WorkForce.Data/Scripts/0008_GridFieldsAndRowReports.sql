-- Hourly production grid = 8 hour rows x 12 handwritten number cells.
-- Field shape is stored so the report engine knows how cells map to RowIndex ((row - 1) * GridColumns + cell).
ALTER TABLE dsi.FieldDefinition ADD
    GridRows    INT NOT NULL CONSTRAINT DF_FieldDefinition_GridRows DEFAULT (1),
    GridColumns INT NOT NULL CONSTRAINT DF_FieldDefinition_GridColumns DEFAULT (1);
GO
-- Per-row reports: one output row per form table row (e.g. per hour) instead of per worksheet.
ALTER TABLE rpt.ReportDefinition ADD RowField NVARCHAR(64) NULL;
GO
-- Which cell of a grid row a report column shows (e.g. column "3" = HOURLY_CELL cell 3).
ALTER TABLE rpt.ReportColumn ADD Cell INT NULL;
GO
