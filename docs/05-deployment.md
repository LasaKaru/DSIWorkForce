# 5. Deployment & operations

## Recommended topology (5,000 sheets/day)

```
[ADF scanners] ──SMB──> \\FILESERVER\Scans\PC-011-A
                              │
                    APP01 (Windows Server, 4–8 vCPU, 16 GB)
                    ├─ DSIWorkForceWorker (Windows Service)
                    └─ IIS: DSI.WorkForce.Web (HTTPS, Windows auth)
                              │
                    SQL01 (existing SQL Server 2016 SP1+; 2019/2022 recommended)
[Optional] GPU01 with Ollama (if using the local VLM recognizer)
```

**Capacity.** Measured on a 20-page test PDF: about **1.2 s per page on a single core**, including PDF rendering,
alignment, OMR, blank detection and image encoding (calibration mode, which also writes overlay PNGs). With
`MaxDegreeOfParallelism: 4` that is roughly 10,000+ pages/hour, so 5,000 sheets take about 25–30 minutes of
processing even if they all arrive at once. A cloud/VLM recognizer adds its own latency; raise parallelism (or add a second
worker server: the queue is safe for multiple workers) if needed.

**Storage.** Aligned images ~150–250 KB/page → ~1 GB/day. Set a retention policy (e.g. keep 13 months online,
then archive). Original PDFs stay in `staging\yyyy\MM\dd` as the legal archive. Database growth is roughly 3–6 GB/year.

## Configuration reference

Worker `appsettings.json`:

| Key | Meaning |
|---|---|
| `Database:ConnectionString` | SQL Server connection (Integrated Security with the service account). |
| `Worker:HotFolders[]` | `Path`, `TemplateCode`, `Station` for each scanner folder. |
| `Worker:StagingRoot / DuplicatesFolder / ImageRoot` | Archive, duplicates, aligned images. |
| `Worker:MaxDegreeOfParallelism` | Pages in parallel (≤ CPU cores). |
| `Worker:MaxAttempts` | After this many failures a file is parked as `Failed` (retry from the dashboard). |
| `Extraction:Recognizer` | `None` / `AzureDocumentIntelligence` / `Ollama`. |
| `Extraction:Azure:*`, `Extraction:Ollama:*` | Recognizer settings (put keys in environment variables, not in the file). |

Web `appsettings.json`: `Database`, `Auth:Mode` (`Windows`), `Auth:ReviewerRoles`, `Auth:AdminRoles`,
`Upload:HotFolders`.

## Monitoring

* Dashboard: queue depth, failed files, worker heartbeat, today's auto-approve rate.
* `GET /health` for your monitoring tool.
* Logs: `logs/worker-*.log`, `logs/web-*.log` (30 days rolling).
* SQL: `SELECT * FROM rpt.vw_QueueStatus`.

## Backups

* SQL Server: full daily + log backups (normal production policy). The queue is in the DB too.
* File shares: `staging` (PDF archive) and `images` with your file-server backup.

## Upgrades

1. Stop the worker service. 2. Run the new DbMigrator. 3. Replace the worker and web binaries. 4. Start the service.
Files that arrive meanwhile wait safely in the hot folder.

## Security notes

* Windows authentication; optional AD-group restriction for reviewers and admins.
* The web app and worker use a least-privilege DB role (`dsi_app`); report readers get `dsi_report_reader`
  (read-only on `rpt`).
* Report expressions are validated against a whitelist; identifiers are bracket-quoted; all values are parameters.
* Scans contain personal data (names, EPF numbers): restrict the share permissions, and don't commit scans to git.
