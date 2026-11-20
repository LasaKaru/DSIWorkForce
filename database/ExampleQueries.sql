/* ---------------------------------------------------------------------------------------------
   Viewing the digitized worksheets directly in SQL Server (SSMS / Azure Data Studio).
   --------------------------------------------------------------------------------------------- */

-- 1. The Daily Efficiency Report exactly like the Excel sheet:
--    one row per HOUR of each worksheet, columns [1]..[12] = the 12 numbers written in that hour's cells
SELECT * FROM rpt.vw_DailyEfficiency
WHERE [Date] = '2026-11-17'
ORDER BY [Resource No], [Shift], [Sheet ID], [Row No];

-- 1b. One row per worksheet (operator / press / shift): hourly totals, rejection, shift efficiency, target %
SELECT * FROM rpt.fn_ShiftSummary('2026-11-17', '2026-11-17');

-- Same thing, fastest form for big date ranges (seeks the WorkDate index):
SELECT * FROM rpt.fn_DailyEfficiency('2026-11-01', '2026-11-30');

-- 2. Efficiency per press for a month (from the hourly rows)
SELECT [Resource No], SUM([No of Units produced]) AS Units, SUM([Rejection]) AS Rejection, AVG([Efficiency]) AS AvgHourlyEfficiency
FROM rpt.fn_DailyEfficiency('2026-11-01', '2026-11-30')
GROUP BY [Resource No]
ORDER BY AvgHourlyEfficiency;

-- 3. Every captured value of one sheet (long format) - what the OCR read and what a reviewer changed
SELECT FieldKey, Label, RowIndex, RawText, ValueText, Confidence, Source, IsCorrected
FROM rpt.vw_WorksheetValues
WHERE WorksheetId = 1
ORDER BY GroupName, FieldKey, RowIndex;

-- 4. Machine checklist items NOT ticked (possible maintenance issue)
SELECT * FROM rpt.vw_ChecklistExceptions WHERE WorkDate >= DATEADD(DAY, -7, CAST(GETDATE() AS DATE));

-- 5. Processing queue health
SELECT * FROM rpt.vw_QueueStatus;
SELECT TOP (50) ScanFileId, FileName, Attempts, LastError FROM dsi.ScanFile WHERE Status = N'Failed' ORDER BY ReceivedAt DESC;

-- 6. Review workload: how many sheets needed a human, and why
SELECT i.Code, COUNT(*) AS Issues
FROM dsi.ValidationIssue i JOIN dsi.Worksheet w ON w.WorksheetId = i.WorksheetId
WHERE w.CreatedAt >= DATEADD(DAY, -1, SYSUTCDATETIME())
GROUP BY i.Code ORDER BY Issues DESC;

-- 7. Who corrected what (audit)
SELECT TOP (100) a.ChangedAt, a.ChangedBy, a.WorksheetId, f.FieldKey, a.RowIndex, a.OldValue, a.NewValue
FROM dsi.ValueAudit a JOIN dsi.FieldDefinition f ON f.FieldId = a.FieldId
ORDER BY a.ChangedAt DESC;

-- 8. Give a supervisor read-only access for Excel / Power BI (Windows login):
-- CREATE USER [DOMAIN\supervisor] FOR LOGIN [DOMAIN\supervisor];
-- ALTER ROLE dsi_report_reader ADD MEMBER [DOMAIN\supervisor];
-- Service accounts for the Worker / Web app:
-- ALTER ROLE dsi_app ADD MEMBER [DOMAIN\svc-dsi-workforce];

/* Excel live connection (no export needed):
   Excel > Data > Get Data > From Database > From SQL Server Database
   Server: YOUR_SQL_SERVER   Database: DSIWorkForce
   Advanced > SQL statement:  SELECT * FROM rpt.fn_DailyEfficiency(DATEADD(DAY,-31,CAST(GETDATE() AS DATE)), CAST(GETDATE() AS DATE))
   Load -> Data > Refresh All updates it any time. */
