-- DSI WorkForce digitization - schemas & roles
-- dsi  = operational tables (scans, worksheets, values, master data)
-- rpt  = report metadata + generated Excel-like views (grant read access to this schema only)
IF SCHEMA_ID(N'dsi') IS NULL EXEC(N'CREATE SCHEMA dsi AUTHORIZATION dbo');
GO
IF SCHEMA_ID(N'rpt') IS NULL EXEC(N'CREATE SCHEMA rpt AUTHORIZATION dbo');
GO
IF DATABASE_PRINCIPAL_ID(N'dsi_app') IS NULL CREATE ROLE dsi_app;
GO
IF DATABASE_PRINCIPAL_ID(N'dsi_report_reader') IS NULL CREATE ROLE dsi_report_reader;
GO
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::dsi TO dsi_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::rpt TO dsi_app;
-- The app regenerates rpt views when report columns change.
GRANT CREATE VIEW TO dsi_app;
GRANT ALTER ON SCHEMA::rpt TO dsi_app;
-- Excel Power Query / Power BI / SSMS users: read-only on the report views.
GRANT SELECT ON SCHEMA::rpt TO dsi_report_reader;
GO
