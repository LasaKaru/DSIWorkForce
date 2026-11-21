# 1. Architecture & data model

## Design goals

| Goal | How it is met |
|---|---|
| 5,000+ sheets/day, **never crash / freeze** | OCR never runs in a UI or web request. A headless Windows Service processes a durable SQL queue in small leased batches with bounded parallelism; one bad page can't stop the service. |
| Nothing lost, nothing twice | SHA-256 de-duplication on ingest; copy → enqueue → delete ordering; one DB transaction per page; `UNIQUE (ScanFileId, PageNo)` makes retries idempotent; expired leases are picked up again automatically. |
| Accurate without a big AI | Template alignment + **OMR** (pixel density) for tick boxes; blank-cell detection so handwriting is read only where something is written; master-data matching and cross-field rules (`ticks × 6 = Total`) catch OCR mistakes. |
| Humans only see exceptions | Confidence per value + business rules → `AutoApproved` or `NeedsReview`. The review screen shows only the cells that need attention, next to the image snippet. |
| "Looks like Excel", also in the DB | Report definitions are metadata; the app generates `rpt.vw_<Report>` (Excel layout) and `rpt.fn_<Report>(@From,@To)`. |
| Easy to add columns | New Excel column = metadata row. New box on the paper = template JSON. No schema change, no recompile. |
| .NET ecosystem only | .NET 8 everywhere: Worker Service, ASP.NET Core Blazor, WPF, Dapper, DbUp, OpenCvSharp, ClosedXML. |

## Processing pipeline (per page)

1. **Render**: PDF page → 200 DPI grayscale (PDFium). Pages are rendered one at a time and disposed.
2. **Align**: find the thick printed border of PC-011-A, perspective-warp it onto an 1800×2642 canvas.
   Every field zone is a fraction (0..1) of that canvas, so scanner DPI, skew and small rotations don't matter.
3. **OMR**: adaptive threshold → ink ratio per box.
   * Check-list boxes and signatures: marked / empty / uncertain.
   * (Optional `OmrTally` extractor for forms where operators tick cells instead of writing numbers.)
4. **Blank detection**: for handwriting boxes, printed table lines are removed (morphology) and the remaining ink
   is measured. Empty boxes are stored as "confidently empty" and never sent to OCR or to a human.
5. **Handwriting** (only non-empty boxes, including each filled cell of the 8 × 12 hourly grid) via `IHandwritingRecognizer`:
   * `Local` (default): PaddleOCR PP-OCRv5 on the CPU inside the Worker. Each box is cleaned first (printed lines
     erased, cropped to the ink, scaled), read in a batch, and re-read with up to 2 other image variants when the result
     is not valid for the field type (a number box must parse as a number). Optional `EscalateTo` sends only unsure
     boxes to Azure / Ollama.
   * `None`: manual keying in the review screen.
   * `AzureDocumentIntelligence`: one call per sheet, words mapped to zones, real per-word confidence.
   * `Ollama`: local vision-language model (e.g. Qwen2.5-VL 7B on one GPU). All snippets of a sheet go into
     one numbered "contact sheet" image → one call → strict JSON.
6. **Normalize**: types, date formats (`yy/M/d` as written on the form), digit confusions, allowed values (shift).
7. **Master data**: EPF must exist (and resemble the written name), press snaps to `dsi.Machine`, tyre text is
   fuzzy-matched to `dsi.Item` (+ learned aliases from reviewer corrections), standard rate comes along.
8. **Computed fields + rules**: hourly units = sum of the row's cells (`Computed`, recalculated after every manual
   correction); required fields, ranges, `SumMatchesTotal` (grid total = *Total Production* box; agreement boosts
   the confidence of the total, the hourly sums and every cell).
9. **Save**: header + values (SqlBulkCopy) + issues in one transaction; aligned JPEG saved for review.

## Data model (SQL Server)

```
dsi.ScanFile          1 row per PDF          queue: Pending → Processing → Completed / Failed (lease, attempts, back-off)
dsi.Worksheet         1 row per page         promoted + indexed: WorkDate, Shift, ResourceNo, EpfNo, ItemCode, Status
dsi.WorksheetValue    1 row per filled cell  (WorksheetId, FieldId, RowIndex) → typed value + raw OCR text + confidence
                                             grid cells: RowIndex = (hour - 1) * 12 + cell
dsi.FieldDefinition   field dictionary       EPF_NO, HOURLY_UNITS, CHK_PRV_UNIT ... (shared by all template versions)
dsi.FormTemplate      layout JSON            PC-011-A v1, v2 ...
dsi.ValidationIssue   why a sheet needs review
dsi.ValueAudit        every manual change (who / old / new / when)
dsi.Employee / Machine / Item / ItemAlias / ShiftMaster   master data
rpt.ReportDefinition / rpt.ReportColumn                   Excel layouts as metadata
rpt.vw_<Code>, rpt.fn_<Code>                              generated Excel-shaped view + fast date-range function
rpt.vw_WorksheetValues, vw_ChecklistExceptions, vw_QueueStatus
```

### Why not one wide table that looks like Excel?
A wide table (Hour1..Hour12, Check1..Check14, ...) needs an `ALTER TABLE` + code change for every new box and
breaks when the form is revised. Here the **storage is narrow and stable** (`WorksheetValue`), and the
**Excel shape is generated** as a view from metadata. So you still get `SELECT * FROM rpt.vw_DailyEfficiency`
returning exactly the Excel columns, and adding a column is a metadata change.
Frequently filtered values are promoted into indexed columns on `dsi.Worksheet`, and the view groups on them,
so filters like `WHERE [Date] = ...` use the index.

Volume: ~120 stored values per sheet (blank cells are not stored) × 5,000 = ~600k rows/day, ~220M rows/year.
That is comfortable for SQL Server with the clustered key `(WorksheetId, FieldId, RowIndex)` and page compression
(roughly 8–15 GB/year; archive or partition by year if wanted).

## Report shapes
* **Per sheet** (e.g. *Shift Summary*): `GROUP BY WorksheetId`.
* **Per row** (e.g. *Daily Efficiency*, `rowField: HOURLY_UNITS`): the worksheet is cross-joined with the hour numbers
  1..8, so each hour becomes a report row; "cell 3" means `RowIndex = (hour - 1) * 12 + 3`, and `Shift.HourStart`
  = shift start + hour − 1. Empty hours are skipped.

## Why a SQL table as the queue (and not RabbitMQ/Hangfire)?
At ~5,000 files/day (a few per minute) SQL Server is more than enough, it is already backed up, and there's
one less product to run. `UPDLOCK, READPAST` lets several worker processes or servers claim work safely.
If volume grows 50×, the `ScanQueueRepository` is the single place to swap for a broker.

## Why Blazor Server for the web app?
It's pure .NET/C# (no separate JS front-end to maintain), works on any PC/tablet browser on the factory network,
and uses Windows (AD) single sign-on. The JSON API (`/api/...`) serves the WPF client and other systems.
The review screen crops image snippets with CSS only, so the server does no image work per request.
