/* ---------------------------------------------------------------------------------------------
   OPTIONAL sample master data for a test database. NOT run by the DbMigrator.
   In production, load these tables from your ERP / HR system (nightly job or SSIS):
     dsi.Employee  <- HR (EPF No, name)
     dsi.Machine   <- maintenance / production planning (press numbers, cavities = UnitsPerTick)
     dsi.Item      <- ERP item master (item code, description, standard rate per hour)
   The better these tables are, the more sheets are auto-approved without a human.
   --------------------------------------------------------------------------------------------- */
SET NOCOUNT ON;

MERGE dsi.Machine AS t
USING (VALUES
    (N'P 24', N'Curing press 24', N'Press', 6.0),
    (N'P 25', N'Curing press 25', N'Press', 6.0),
    (N'P 26', N'Curing press 26', N'Press', 4.0)
) AS s (ResourceNo, Name, Section, UnitsPerTick)
ON t.ResourceNo = s.ResourceNo
WHEN NOT MATCHED THEN INSERT (ResourceNo, Name, Section, UnitsPerTick) VALUES (s.ResourceNo, s.Name, s.Section, s.UnitsPerTick);

MERGE dsi.Item AS t
USING (VALUES
    (N'TYR-0094', N'29 2.10 SRI 94 Full Black', N'Bicycle tyre', 26.00),
    (N'TYR-0068', N'29 2.10 SRI 68 Full Black', N'Bicycle tyre', 26.00)
) AS s (ItemCode, Description, Category, StandardRatePerHour)
ON t.ItemCode = s.ItemCode
WHEN NOT MATCHED THEN INSERT (ItemCode, Description, Category, StandardRatePerHour) VALUES (s.ItemCode, s.Description, s.Category, s.StandardRatePerHour);

-- Employees: import from HR. Example shape only:
-- INSERT dsi.Employee (EpfNo, Name, Section) VALUES (N'1234', N'Employee Name', N'Press');
