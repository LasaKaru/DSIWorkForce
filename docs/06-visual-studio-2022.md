# 6. Run the solution in Visual Studio 2022

This guide takes you from a fresh Windows PC to the whole system running on your machine:
database, Worker, web app and WPF app. It takes about 30–45 minutes the first time.

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

## 3. Create the local folders

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

## 4. Create the database

1. In Solution Explorer right-click **DSI.WorkForce.DbMigrator → Set as Startup Project**.
2. In the toolbar, open the dropdown next to the green ▶ button and choose the launch profile:
   * **Create/upgrade DB (LocalDB)**, if you use LocalDB, or
   * **Create/upgrade DB (localhost)**, if you installed SQL Server Developer / Express as the default instance.
   * For another server, edit `src/DSI.WorkForce.DbMigrator/Properties/launchSettings.json` and change `Server=...`.
3. Press **Ctrl+F5** (run without debugging). You should see:

```
8 migration script(s) to apply.
...
Template PC-011-A v1: imported - 37 fields
Report DailyEfficiency: imported, view rpt.vw_DailyEfficiency built
Report ShiftSummary: imported, view rpt.vw_ShiftSummary built
All report views rebuilt. Done.
```

You can run it again any time. It only applies new scripts. Use the profile
**Re-import report layouts** to overwrite report columns with the JSON files in `templates/reports`.

### Add test master data (recommended)

Open `database/SampleMasterData.sql`. In Visual Studio you can use **View → SQL Server Object Explorer**:
connect to `(localdb)\MSSQLLocalDB` or `localhost`, right-click database **DSIWorkForce → New Query**, paste the
script and press the ▶ *Execute* button. Or open it in SSMS. Also add a test employee:

```sql
INSERT dsi.Employee (EpfNo, Name, Section) VALUES (N'1234', N'Test Operator', N'Press');
```

---

## 5. Point Worker and Web at your database

Edit the **connection string** in both files if you are **not** using `localhost`:

* `src/DSI.WorkForce.Worker/appsettings.Development.json`
* `src/DSI.WorkForce.Web/appsettings.Development.json`

```json
"Database": {
  "ConnectionString": "Server=(localdb)\\MSSQLLocalDB;Database=DSIWorkForce;Integrated Security=true;TrustServerCertificate=true"
}
```

(In JSON, `\` must be written as `\\`.)

In Development the web app runs **without login** (`"Auth": { "Mode": "None" }`). Production uses Windows login.

---

## 6. Run Worker + Web (+ WPF) together

1. Right-click the **Solution** → **Configure Startup Projects…** → **Multiple startup projects**:

   | Project | Action |
   |---|---|
   | DSI.WorkForce.Worker | **Start** |
   | DSI.WorkForce.Web | **Start** |
   | DSI.WorkForce.Desktop | Start (optional) |
   | all others | None |

2. For the Web project choose the **https** launch profile (toolbar dropdown) → URL `https://localhost:7082`.
3. Press **F5**.

   * A console window opens for the **Worker**: `Watching C:\DSIWorkForce\hot\PC-011-A for template PC-011-A`.
   * The browser opens the **Dashboard**. *Worker heartbeat* shows a time, which means both are talking to the database.
   * The **WPF** window opens (its API address is in `src/DSI.WorkForce.Desktop/desktopsettings.json`,
     default `https://localhost:7082/`).

---

## 7. Try it end to end

1. **Scan in:** copy `C:\DSIWorkForce\samples\sample.pdf` into `C:\DSIWorkForce\hot\PC-011-A`
   (or use the web **Upload scans** page, or WPF **Scan station → Add scanned files…**).
2. Within ~10 seconds the Worker console shows:
   `Queued sample.pdf ...` then `Page 1/1 -> Worksheet 1: NeedsReview, ...`.
3. **Review** (web *Review* tab or WPF *Review* tab) → open the sheet:
   * left: the straightened scan; the box you are editing is highlighted;
   * right: only the fields that need a human. Type the date (as written, e.g. `26/11/17`), press, EPF, shift,
     tyre, total…;
   * **hourly grid**: type the number of each filled cell. Row sums update live, and the total turns ✓ when it matches
     *Total Production*;
   * **Verify ✓ & next**.
4. **Reports** → choose *Daily Efficiency Report*, set the date range to the **date written on the sheet** →
   **Show** → one row per hour, exactly like the Excel → **Export to Excel**.
5. **Database level:** SQL Server Object Explorer → DSIWorkForce → **Views → rpt.vw_DailyEfficiency** →
   right-click **View Data**. More examples: `database/ExampleQueries.sql`.

> **Why is my sheet not in the report?** Reports filter on the date *written on the sheet*. With the default
> `"Recognizer": "None"` nothing handwritten is read automatically, so the date is empty until a reviewer types
> it on the review screen. Sheets still waiting for review are included (not only verified ones) unless the report
> has *Only approved / verified sheets* ticked.

---

## 8. Calibrate the form layout (when using new scans / scanners)

1. Set **DSI.WorkForce.Worker** as startup project, choose the launch profile **Calibrate sample**
   (it reads `C:\DSIWorkForce\samples\sample.pdf`), press **Ctrl+F5**.
2. Open `C:\DSIWorkForce\samples\calibration\sample_p1_overlay.png`:
   green = read automatically, red = handwriting to read/type, orange = empty, grey = not found.
3. If a box is misplaced, change its `zone` in `templates/forms/PC-011-A.v1.json`, run again, and repeat.
   When happy, run the DbMigrator (step 4) so the database gets the new layout.

To calibrate another file: Worker project → **Properties → Debug → Open debug launch profiles UI** → edit
*Command line arguments*: `--calibrate C:\path\file.pdf`.

---

## 9. Run the tests

**Test → Test Explorer → Run All Tests** (49 tests).

To also run the real-scan integration test, set an environment variable, then restart Visual Studio:

```powershell
setx DSI_SAMPLE_PDF C:\DSIWorkForce\samples\sample.pdf
```

---

## 10. Turn on automatic handwriting reading (optional)

Keep keys out of the code: right-click **DSI.WorkForce.Worker → Manage User Secrets** and add, for example:

```json
{
  "Extraction": {
    "Recognizer": "AzureDocumentIntelligence",
    "Azure": { "Endpoint": "https://<your-resource>.cognitiveservices.azure.com/", "ApiKey": "<key>" }
  }
}
```

or, for a local GPU model served by Ollama (`ollama pull qwen2.5vl:7b`):

```json
{ "Extraction": { "Recognizer": "Ollama", "Ollama": { "BaseUrl": "http://localhost:11434", "Model": "qwen2.5vl:7b" } } }
```

Restart the Worker. New sheets will arrive with most fields already filled in.

---

## 11. Troubleshooting

| Symptom | Fix |
|---|---|
| DbMigrator: *Cannot connect to SQL Server* | Wrong server name, or SQL Server not running. For LocalDB run `sqllocaldb start MSSQLLocalDB`. |
| Worker: *No active template 'PC-011-A'* | Run the DbMigrator (step 4). |
| Worker: *Hot folder ... not reachable* | Create the folders (step 3) or fix `Worker:HotFolders` in `appsettings.Development.json`. |
| Dashboard: *Worker heartbeat: never* | The Worker isn't running, or it uses a different connection string than the Web app. |
| Sheet status *NeedsReview* with *ALIGNMENT_FAILED* | Page is not PC-011-A, or very skewed/cut off. Rescan; check with *Calibrate*. |
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
