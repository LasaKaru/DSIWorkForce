# 6. Run the solution in Visual Studio 2022

This guide takes you from a fresh Windows PC to the whole system running on your machine:
database, Worker, web app and WPF app. It takes about 30–45 minutes the first time.

**Short version:** install VS 2022 (with LocalDB) → open `DSIWorkForce.sln` → set **Web + Worker** as startup projects (step 5) → **F5**. The database is created automatically
in `(localdb)\MSSQLLocalDB` with Windows authentication.

---

## 1. Install the prerequisites (one time)

| What | Where / notes |
|---|---|
| **Visual Studio 2022** (version 17.8 or newer) | Visual Studio Installer, then tick these workloads: **ASP.NET and web development**, **.NET desktop development**, **Data storage and processing** (installs SQL Server Express **LocalDB**). |
| **.NET 8 SDK** | Comes with VS 17.8+. Check in a terminal: `dotnet --list-sdks` should show `8.0.x`. |
| **SQL Server** | Use either **LocalDB** (installed with the workload above, simplest) **or** SQL Server 2019/2022 **Developer** edition (free). Your company SQL Server also works if you have rights to create a database. |
| **SSMS** (SQL Server Management Studio) | Optional, but useful to look at the data and run the `.sql` files. |
| **Git** | Comes with Visual Studio. |

Trust the local HTTPS development certificate once (Developer PowerShell: *View → Terminal*):

```powershell
dotnet dev-certs https --trust
```

---

## 2. Get the code

1. Start Visual Studio → **Clone a repository**.
2. Repository location: `https://github.com/LasaKaru/DSIWorkForce` → choose a local path → **Clone**.
3. Switch to the branch with the code: **Git → Manage Branches** → under *remotes/origin* double-click
   **`claude/brave-edison-n3zzmw`** (or merge it into `main` first).
4. **File → Open → Project/Solution** → `DSIWorkForce.sln`.

Solution Explorer shows 8 projects:

```
DSI.WorkForce.Core          shared business logic
DSI.WorkForce.Data          SQL scripts + data access
DSI.WorkForce.Extraction    PDF / OpenCV / OCR
DSI.WorkForce.Worker        background processing (Windows Service)
DSI.WorkForce.DbMigrator    creates / upgrades the database
DSI.WorkForce.Web           web app (Blazor) + API
DSI.WorkForce.Desktop       WPF app
DSI.WorkForce.Tests         unit tests
```

5. **Build → Build Solution** (`Ctrl+Shift+B`). NuGet packages restore automatically on the first build.
   All package versions are in `Directory.Packages.props`.

> If the build fails with *"OpenCvSharpExtern"* or *"pdfium"* errors when running, set the platform to **x64**:
> Build → Configuration Manager → Active solution platform → *Any CPU* is fine on 64-bit Windows; just make sure
> no project has *Prefer 32-bit* ticked.

---

## 3. Local folders

The Worker creates these folders automatically on first start (you can also create them yourself).
The development settings (`appsettings.Development.json` of Worker and Web) use these folders.
In **View → Terminal** (PowerShell):

```powershell
New-Item -ItemType Directory -Force -Path C:\DSIWorkForce\hot\PC-011-A, C:\DSIWorkForce\staging, C:\DSIWorkForce\duplicates, C:\DSIWorkForce\images, C:\DSIWorkForce\samples
```

| Folder | Purpose |
|---|---|
| `C:\DSIWorkForce\hot\PC-011-A` | **Drop scanned PDFs here.** This acts as the scanner's output folder. |
| `C:\DSIWorkForce\staging` | Processed PDFs are archived here (by date). |
| `C:\DSIWorkForce\images` | Straightened page images shown on the review screen. |
| `C:\DSIWorkForce\duplicates` | The same PDF dropped twice ends up here. |
| `C:\DSIWorkForce\samples` | Put a few test scans here for calibration. |

Copy your sample scan (e.g. `DSI OCR.pdf`) to `C:\DSIWorkForce\samples\sample.pdf`.

---

## 4. Database: created automatically (Windows authentication, LocalDB)

All projects use the same connection string, with **Windows authentication** (your Windows login, no SQL password):

```
Server=(localdb)\MSSQLLocalDB;Database=DSIWorkForce;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=60
```

It is set in `appsettings.json` / `appsettings.Development.json` of **Web**, **Worker** and **DbMigrator**.

When you press **F5**, the Web app and the Worker (Development mode: `"AutoMigrate": true`) automatically:

1. **create the database `DSIWorkForce`** in LocalDB if it does not exist,
2. apply new migration scripts (tables, views, roles),
3. import the PC-011-A form template and the report layouts (*Daily Efficiency*, *Shift Summary*),
4. build the report views `rpt.vw_DailyEfficiency`, `rpt.vw_ShiftSummary`,
5. load **sample master data** (`"SeedSampleData": true`): presses P 24–P 26, two tyre items with standard
   rate 26/hour, and test employee **EPF 1234 "Test Operator"**.

Nothing else is needed. The Web/Worker log shows `Database is up to date.` when done.
The first start of LocalDB can take 10–30 seconds.

Check that LocalDB is installed (Developer PowerShell):

```powershell
sqllocaldb info              # should list MSSQLLocalDB
sqllocaldb start MSSQLLocalDB
```

If it is missing: Visual Studio Installer → *Modify* → **Data storage and processing** workload (or *Individual
components → SQL Server Express LocalDB*).

**Optional: DbMigrator.** You only need it to prepare a database without starting the apps, or to reset report
layouts. Set it as startup project and choose a launch profile from the ▶ dropdown:
*LocalDB (appsettings.json)*, *LocalDB + sample data*, or *Re-import report layouts*. Then press Ctrl+F5.

> LocalDB is for development on your own PC. On the server, use a real SQL Server and set
> `"AutoMigrate": false`. Run the DbMigrator once per release (see [05-deployment.md](05-deployment.md)).

---

## 5. What to set as the startup project

You need **two programs running at the same time**: the **Worker** (reads the scans) and the **Web** app
(review, reports). The WPF app is optional.

1. Right-click the **Solution 'DSIWorkForce'** → **Configure Startup Projects…**
2. Choose **Multiple startup projects** and set:

   | Project | Action |
   |---|---|
   | **DSI.WorkForce.Web** | **Start** |
   | **DSI.WorkForce.Worker** | **Start** |
   | DSI.WorkForce.Desktop | Start (optional, WPF app) |
   | DSI.WorkForce.Core / Data / Extraction / DbMigrator / Tests | None |

3. Optional: use the arrows to move **Web** to the top, so it starts first.
4. Click **OK**. The toolbar ▶ button now says **Start**.

> Only want to look at the website? Set **DSI.WorkForce.Web** alone as startup project (right-click →
> *Set as Startup Project*). Scans won't be processed without the Worker, though.

---

## 6. Run it

1. Press **F5** (with debugging) or **Ctrl+F5** (faster, without debugging).
2. A console window opens for the **Worker**:
   ```
   Database: (localdb)\MSSQLLocalDB / DSIWorkForce (Windows authentication: True)
   ... Database is up to date.
   Watching C:\DSIWorkForce\hot\PC-011-A for template PC-011-A
   Extraction worker ... started
   ```
3. The browser opens the **Dashboard** at `https://localhost:7082` (in Development there is no login).
   *Worker heartbeat* shows a time, which means Web and Worker both reach the database.
4. If you started it, the **WPF** window opens. It talks to `https://localhost:7082/` (`desktopsettings.json`).
5. Stop everything with **Shift+F5**.

---

## 7. Try it end to end

1. **Scan in:** copy `C:\DSIWorkForce\samples\sample.pdf` into `C:\DSIWorkForce\hot\PC-011-A`
   (or use the web **Upload scans** page, or WPF **Scan station → Add scanned files…**).
2. Within ~10 seconds the Worker console shows:
   `Queued sample.pdf ...` then `Page 1/1 -> Worksheet 1: NeedsReview, ...`.
3. **Review** (web *Review* tab or WPF *Review* tab) → open the sheet:
   * left: the straightened scan; the box you are editing is highlighted;
   * right: only the fields that need a human. Type the date (as written, e.g. `26/11/17`), press (`P 24`),
     EPF (`1234` = the sample test employee), shift (`2-10`), tyre (`29 2.10 SRI 94 Full Black`), total…;
   * **hourly grid**: type the number of each filled cell. Row sums update live, and the total turns ✓ when it matches
     *Total Production*;
   * **Verify ✓ & next**.
4. **Reports** → choose *Daily Efficiency Report*, set the date range to the **date written on the sheet** →
   **Show** → one row per hour, exactly like the Excel → **Export to Excel**.
5. **Database level:** SQL Server Object Explorer → DSIWorkForce → **Views → rpt.vw_DailyEfficiency** →
   right-click **View Data**. More examples: `database/ExampleQueries.sql`.

> **Why is my sheet not in the report?** Reports filter on the date *written on the sheet*. Dates with slashes
> are the hardest box for OCR, so the date often waits for a reviewer to confirm or type it on the review screen. Sheets still waiting for review are included (not only verified ones) unless the report
> has *Only approved / verified sheets* ticked.

---

## 8. Calibrate the form layout (when using new scans / scanners)

1. Set **DSI.WorkForce.Worker** as startup project, choose the launch profile **Calibrate sample**
   (it reads `C:\DSIWorkForce\samples\sample.pdf`), press **Ctrl+F5**.
2. Open `C:\DSIWorkForce\samples\calibration\sample_p1_overlay.png`:
   green = read automatically, red = handwriting to read/type, orange = empty, grey = not found.
3. If a box is misplaced, change its `zone` in `templates/forms/PC-011-A.v1.json`, run again, and repeat.
   When happy, restart the apps (or run the DbMigrator) so the database gets the new layout.

To calibrate another file: Worker project → **Properties → Debug → Open debug launch profiles UI** → edit
*Command line arguments*: `--calibrate C:\path\file.pdf`.

---

## 9. Run the tests

**Test → Test Explorer → Run All Tests** (53 tests).

To also run the real-scan integration test, set an environment variable, then restart Visual Studio:

```powershell
setx DSI_SAMPLE_PDF C:\DSIWorkForce\samples\sample.pdf
setx DSI_TEST_LOCAL_OCR 1      # also run the real handwriting-OCR test
```

---

## 10. Handwriting reading (OCR)

Handwriting is read **automatically by default** with PaddleOCR (PP-OCRv5) running on your CPU inside the Worker.
It is free and offline, and the model ships with the NuGet package. Settings in `src/DSI.WorkForce.Worker/appsettings.json`:

```json
"Extraction": {
  "Recognizer": "Local",
  "EscalateTo": "None",
  "EscalateBelowConfidence": 0.85,
  "Local": { "Model": "PPOCRv5", "Variants": 3, "CpuThreads": 2 }
}
```

The first page after starting takes a few extra seconds (model loading: log line *Loading PaddleOCR model*).
Values the OCR read are pre-filled on the review screen. Low-confidence ones are highlighted for checking.
Sheets read before OCR was on: open them and click **↻ Re-read**.

To see what the OCR read for each box (all image variants), run the calibration (step 8) with debug logging:
set the environment variable `Serilog__MinimumLevel__Default=Debug` in the Worker's launch profile.

**Second opinion for unsure boxes (optional).** Keep keys out of the code: right-click
**DSI.WorkForce.Worker → Manage User Secrets** and add, for example:

```json
{
  "Extraction": {
    "EscalateTo": "AzureDocumentIntelligence",
    "Azure": { "Endpoint": "https://<your-resource>.cognitiveservices.azure.com/", "ApiKey": "<key>" }
  }
}
```

or, for a local GPU model served by Ollama (`ollama pull qwen2.5vl:7b`):

```json
{ "Extraction": { "EscalateTo": "Ollama", "Ollama": { "BaseUrl": "http://localhost:11434", "Model": "qwen2.5vl:7b" } } }
```

Restart the Worker. Only boxes the local OCR is unsure about (confidence < 0.85) are sent to the second engine.
To use Azure or Ollama for *everything*, set `"Recognizer"` instead of `"EscalateTo"`.

---

## 11. Troubleshooting

| Symptom | Fix |
|---|---|
| *Automatic database setup failed* / *Cannot connect to SQL Server* | LocalDB not installed or not started: `sqllocaldb info`, `sqllocaldb start MSSQLLocalDB` (step 4). |
| *Login failed for user 'DOMAIN\you'* | You are using a real SQL Server: ask the DBA to add your Windows login, or switch back to LocalDB. |
| Worker: *No active template 'PC-011-A'* | The automatic setup did not run or failed: check the first lines of the Worker log, or run the DbMigrator (step 4). |
| Worker: *Hot folder ... not reachable* | Create the folders (step 3) or fix `Worker:HotFolders` in `appsettings.Development.json`. |
| Dashboard: *Worker heartbeat: never* | The Worker isn't running, or it uses a different connection string than the Web app. |
| Sheet status *NeedsReview* with *ALIGNMENT_FAILED* | Page is not PC-011-A, or very skewed/cut off. Rescan; check with *Calibrate*. |
| `DllNotFoundException: paddle_inference_c` / OCR engine fails to load | Run as 64-bit and install the latest *Microsoft Visual C++ Redistributable (x64)*. As a fallback set `"Recognizer": "None"`. |
| `DllNotFoundException: OpenCvSharpExtern` / `pdfium` | Run as 64-bit (see step 2). Install the latest *Microsoft Visual C++ Redistributable (x64)*. |
| WPF: *Server not reachable* | Start the Web project too, and check `ApiBaseUrl` in `desktopsettings.json`. Run `dotnet dev-certs https --trust`. |
| Browser certificate warning | `dotnet dev-certs https --trust`, then restart the browser. |
| Port 7082 already used | Change `applicationUrl` in `src/DSI.WorkForce.Web/Properties/launchSettings.json` (and `ApiBaseUrl` for WPF). |
| Report shows old columns after an update | Run the DbMigrator profile **Re-import report layouts**, or edit them in *Report columns*. |

---

## 12. Publishing for the server (when ready)

Right-click a project → **Publish → Folder**:

* **DSI.WorkForce.Worker** → install as a Windows Service ([05-deployment.md](05-deployment.md), [02-step-by-step-guide.md](02-step-by-step-guide.md) step 6)
* **DSI.WorkForce.Web** → IIS with Windows Authentication (step 7 of the guide)
* **DSI.WorkForce.DbMigrator** → run once per release against the production database
* **DSI.WorkForce.Desktop** → copy the folder to scan-station PCs (set `ApiBaseUrl` to the server URL)
