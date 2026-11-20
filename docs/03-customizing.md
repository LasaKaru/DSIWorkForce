# 3. Customizing: add columns, fields and forms

There are two layers, and most changes only touch one of them:

| You want to… | Change | Code change? |
|---|---|---|
| Add / remove / rename / reorder a column in the Excel-style report | Report metadata (`rpt.ReportColumn`) | No |
| Show a value the form already captures (e.g. curing temperature) | Report metadata | No |
| Add a calculated column (target %, rejection %) | Report metadata (Expression) | No |
| Capture a **new box on the paper form** | Template JSON (new version) + report metadata | No |
| Add a completely new form (another section) | New template JSON + hot folder + report | No |
| Join a new master table / new header column | `ReportViewSqlBuilder` whitelist | Small |

## A. Add a column to the report (no code)

**Web:** *Report columns* → **+ Add column** → fill in → **Save & rebuild view**.
The view `rpt.vw_DailyEfficiency`, the web grid, the Excel export and the WPF grid all show it immediately.

Column source types:

| SourceType | Source | Example |
|---|---|---|
| `Header` | WorkDate, Shift, ResourceNo, EpfNo, ItemCode, Status, WorksheetId, TemplateCode, PageNo, MinConfidence, CreatedAt, VerifiedBy, VerifiedAt, SourceFile | `Date` ← WorkDate |
| `Field` | a field key + `RowIndex` (0 = single box, 1..n = table row) | Hour `3` ← `HOURLY_UNITS` row 3 |
| `FieldAggregate` | field key + `SUM / MIN / MAX / AVG / COUNT / LIST` | `Reason` ← LIST of `HOURLY_REASON` |
| `Lookup` | Item.Description, Item.StandardRatePerHour, Item.Category, Employee.Name, Employee.Section, Machine.Name, Machine.Section, Machine.UnitsPerTick, Shift.StartTime, Shift.EndTime, Shift.Hours | `Standard Rate per Hour` |
| `Expression` | formula over other columns' **keys** in `{}` | `{Units} / NULLIF({TargetQty}, 0)` |

**Report shape.** *One report row per…* (`rowField`): empty = one row per worksheet; `HOURLY_UNITS` = one row per
hour, like the Excel. In a per-hour report, table fields (HOURLY_REASON, HOURLY_REJECTION…) automatically mean
"this hour", `HOURLY_CELL` + **Cell** `1..12` is a cell of this hour, and `RowNo`, `Shift.HourStart`,
`Shift.HourEnd` are available. In a per-sheet report choose a **Row** (e.g. `HOURLY_UNITS` row 3 = hour 3) or use an
aggregate over all hours.

**Example: the Rejection column** (the orange column on the form, field `HOURLY_REJECTION`) is already in both
reports. To add it yourself: *+ Add column* → Key `Rejection`, Header `Rejection`, Source type `Field`, Source
`HOURLY_REJECTION` (per-hour report) or `FieldAggregate` + `SUM` (per-sheet report) → *Save & rebuild view*.

Expressions may use numbers, `+ - * / ( )`, comparisons and `NULLIF, ISNULL, COALESCE, ROUND, ABS, CEILING, FLOOR,
CASE WHEN THEN ELSE END, CAST AS DECIMAL/INT/FLOAT`. Anything else (quotes, `;`, comments, sub-queries) is rejected,
so admins can't break or inject SQL. Untick **Visible** to keep a helper column (like `HoursWorked`) out of the report.

**Same thing in SQL** (e.g. from SSMS), then rebuild from the admin page or by re-running the DbMigrator:
```sql
INSERT rpt.ReportColumn (ReportId, ColumnOrder, ColumnKey, Header, SourceType, Source, RowIndex, Format, IsVisible)
SELECT ReportId, 315, N'CuringTemp', N'Curing Temp', N'Field', N'CURING_TEMP', 0, NULL, 1
FROM rpt.ReportDefinition WHERE Code = N'DailyEfficiency';
```

**Examples**
* Target achievement: key `TargetPct`, Expression `{Units} / NULLIF({TargetQty}, 0)`, format `0.0%`.
* Rejection rate (Daily Efficiency, per hour): Expression `{Rejection} / NULLIF({Units}, 0)`, format `0.00%`.
* Good units: Expression `{Units} - ISNULL({Rejection}, 0)`.
* A checklist item: `Field` `CHK_PRV_UNIT` → shows 1/0.
* Efficiency on good units only: `({Units} - ISNULL({Rejection}, 0)) / NULLIF({StdRate}, 0)`.

## B. Capture a new box from the paper form

1. Copy `templates/forms/PC-011-A.v1.json` to `PC-011-A.v2.json`, set `"version": 2`.
2. Add a field, e.g.
   ```json
   { "key": "MOULD_CHANGE_COUNT", "label": "Mould changes", "group": "Header",
     "type": "Integer", "extractor": "Handwriting", "zone": { "x": 0.70, "y": 0.10, "w": 0.10, "h": 0.03 },
     "min": 0, "max": 20 }
   ```
   * `type`: Text, Integer, Decimal, Date, Time, Checkbox
   * `extractor`: `Omr` (tick box), `OmrTally` (row of tick cells; `rows`, `columns`, `unitsPerTick`),
     `Computed` (`sumOf` a grid field: one sum per row),
     `Handwriting`, `Manual` (always typed by a reviewer)
   * optional: `required`, `min`, `max`, `allowedValues`, `dateFormats`, `lookup` (Employee/Machine/Item),
     `promoteTo` (WorkDate/Shift/ResourceNo/EpfNo/ItemCode), `rows` for table columns, `hint` for AI recognizers.
   * **Never rename an existing `key`**: reports refer to it. Labels can change freely.
3. Find the zone with `DSI.WorkForce.Worker.exe --calibrate scan.pdf --template PC-011-A.v2.json`.
4. Copy the JSON next to the DbMigrator (it ships `templates/**`) and run it. The newest version becomes active;
   sheets already processed keep their original version.
5. Add a report column for the new field (section A).

## C. Add a new form type (e.g. another section's worksheet)

1. Create `templates/forms/XX-001.v1.json` with its fields (start from the PC-011-A file).
2. Add a hot folder in the worker config: `{ "Path": "\\\\FILESERVER\\Scans\\XX-001", "TemplateCode": "XX-001" }`
   and in the web `Upload:HotFolders`.
3. Run the DbMigrator, calibrate, then create a report in *Report columns* (code e.g. `XxDaily`) with
   `templateCodes: ["XX-001"]`, or add `templates/reports/XxDaily.json` and run the DbMigrator.

## D. Developer extensions

* New lookup or header column: add it to the whitelists in `src/DSI.WorkForce.Core/Reporting/ReportViewSqlBuilder.cs`.
* New cross-field rule: add a case in `WorksheetValidator.ApplyRule` and reference it from the template `rules`.
* New recognizer (e.g. PaddleOCR service): implement `IHandwritingRecognizer` and register it in
  `ExtractionServiceCollectionExtensions`.
