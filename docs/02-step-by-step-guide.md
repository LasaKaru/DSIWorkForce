# 2. Step-by-step rollout guide

A realistic order to go from paper to production. Each step is useful on its own.

## Step 0: Decide two things

1. **Handwriting recognizer** (can change later in config, no code change):

   | Option | Cost | Accuracy on handwriting | Notes |
   |---|---|---|---|
   | `None` (manual keying) | free | n/a (human types) | Start here for the pilot. Tick boxes are read automatically and empty boxes are skipped, but every **filled hourly cell** (~60–90 per sheet) plus ~24 other boxes must be typed (fast 8 × 12 entry grid with live row sums). Not practical for 5,000 sheets/day long-term. |
   | `AzureDocumentIntelligence` | per page (cloud) | very good for digits/Latin text | ~150k pages/month at 5,000/day, so check current Azure pricing. Needs internet from the worker server. |
   | `Ollama` (local VLM, e.g. `qwen2.5vl:7b`) | one GPU PC (e.g. RTX 4060 Ti 16 GB / 4090) | good, and improves with newer models | Fully on-premise. One call per sheet. |

   Because every hourly cell now holds a handwritten number, a recognizer is needed for full volume. Single digits
   in boxed cells are the easiest kind of handwriting, and the *Total Production* cross-check catches misreads.
   Sinhala handwriting (the *reason* column) is the hardest for every engine, which is why the form-redesign doc
   suggests reason **codes** as tick boxes.
2. **Where it runs**: one Windows Server VM (4–8 cores, 16 GB RAM) for Worker + Web is enough for 5,000 sheets/day.
   SQL Server can be your existing instance.

## Step 1: Create the database

```powershell
dotnet publish src/DSI.WorkForce.DbMigrator -c Release -o C:\apps\dsi-migrator
C:\apps\dsi-migrator\DSI.WorkForce.DbMigrator.exe --create-db --Database:ConnectionString="Server=SQL01;Database=DSIWorkForce;Integrated Security=true;TrustServerCertificate=true"
```
This creates schemas `dsi` and `rpt`, all tables, imports the PC-011-A template and builds `rpt.vw_DailyEfficiency`.
Re-run it after every upgrade (scripts run only once; see `dbo.SchemaVersions`).

## Step 2: Load master data

Fill `dsi.Employee` (EPF No, Name), `dsi.Machine` (press numbers like `P 24`),
`dsi.Item` (item code, description exactly as in ERP, standard rate per hour). Best: a nightly SQL Agent job
from HR/ERP. `database/SampleMasterData.sql` shows the shape. **Good master data = more auto-approved sheets.**

## Step 3: Shared folders & accounts

| Folder | Who needs access |
|---|---|
| `\\FILESERVER\Scans\PC-011-A` (hot folder) | scanners/scan PCs: write · worker: modify · web app: write (uploads) |
| `D:\DSIWorkForce\staging` (PDF archive) | worker: modify |
| `D:\DSIWorkForce\images` (aligned page images) | worker: modify · web app: read |

Create a service account (e.g. `DOMAIN\svc-dsi-workforce`) for the Worker service and the IIS app pool, and add it to the
database role `dsi_app` (see `database/ExampleQueries.sql`).

## Step 4: Configure the scanners

* 200–300 DPI, **grayscale** (not black & white: OMR thresholds work better on gray), PDF output.
* Multi-page PDFs are fine (one sheet per page). Duplex off unless the back is used.
* Auto-rotate/deskew on if the scanner supports it.
* Save directly into the hot folder (or into a local folder uploaded by the WPF *Scan station*).

## Step 5: Calibrate the template on real scans

```powershell
DSI.WorkForce.Worker.exe --calibrate C:\samples\batch1.pdf --out C:\samples\calibration
```
Open `*_overlay.png`: every box of the form has a rectangle: green = value found, orange = empty, red = needs review,
and every hourly cell has its own rectangle. If a zone is off, adjust its `zone` in
`templates/forms/PC-011-A.v1.json` (fractions of the form border: x, y, w, h). Try 20–30 sheets from different
scanners/operators. If faint pencil numbers are treated as empty, lower `Extraction:BlankInkThreshold`; if
smudges are treated as written, raise it. Tune `omr.markThreshold` only if faint checklist ticks are missed.

## Step 6: Install the Worker as a Windows Service

```powershell
dotnet publish src/DSI.WorkForce.Worker -c Release -r win-x64 --self-contained false -o C:\apps\dsi-worker
# edit C:\apps\dsi-worker\appsettings.json  (connection string, hot folders, paths, recognizer)
sc.exe create DSIWorkForceWorker binPath= "C:\apps\dsi-worker\DSI.WorkForce.Worker.exe" start= delayed-auto obj= "DOMAIN\svc-dsi-workforce" password= "..."
sc.exe failure DSIWorkForceWorker reset= 86400 actions= restart/60000/restart/60000/restart/60000
sc.exe start DSIWorkForceWorker
```
Logs: `C:\apps\dsi-worker\logs\worker-YYYYMMDD.log`. The dashboard shows the worker heartbeat.

## Step 7: Deploy the web app (IIS, Windows login)

1. Install the **ASP.NET Core 8 Hosting Bundle** on the server.
2. `dotnet publish src/DSI.WorkForce.Web -c Release -o C:\apps\dsi-web`
3. IIS site → app pool *No Managed Code*, identity = service account. Enable **Windows Authentication** and disable
   Anonymous on the site.
4. `appsettings.json`: connection string, `Upload:HotFolders`, and optionally AD groups in
   `Auth:ReviewerRoles` / `Auth:AdminRoles`.
5. Browse to `https://dsi-workforce/` → Dashboard.

## Step 8: Pilot (1–2 weeks, one press line)

* Scan a day's sheets, review in **Review** (Verify ✓ & next), compare **Reports** with the manual Excel.
* Track: auto-approved %, average review time per sheet, top issue codes (`database/ExampleQueries.sql` #6).
* Fix master data (missing EPFs/items) as they show up. Item corrections teach `dsi.ItemAlias` automatically.

## Step 9: Turn on a handwriting recognizer

Set `Extraction:Recognizer` to `AzureDocumentIntelligence` or `Ollama` in the worker config and restart the service.
Keep the key out of the file: `setx /M Extraction__Azure__ApiKey ...` (environment variable) or a secret store.

## Step 10: Reporting at the database level

* SSMS: `SELECT * FROM rpt.vw_DailyEfficiency WHERE [Date] = '2026-11-17'`
* Excel: *Data → Get Data → SQL Server* → `SELECT * FROM rpt.fn_DailyEfficiency(...)` → Refresh All.
* Power BI: connect to `rpt.vw_DailyEfficiency` / `rpt.vw_WorksheetValues`.
* Give read-only users the role `dsi_report_reader`.

## Step 11: Revise the paper form (biggest accuracy win)

See [04-form-redesign.md](04-form-redesign.md). Print the new version, create `PC-011-A.v2.json`, and run the
DbMigrator. Old and new sheets keep feeding the same report.

## Step 12 (later): Go paperless at the press

The same tables can be filled from a tablet screen at each press (Blazor page: tap EPF badge, pick item, tick the
checklist, enter hourly counts). Reports, views and Excel connections stay unchanged.
