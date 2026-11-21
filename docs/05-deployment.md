# 5. Deployment & operations

## Recommended topology (5,000 sheets/day)

```
[ADF scanners] ──SMB──> \\FILESERVER\Scans\PC-011-A
                              │
                    APP01 (Windows Server, 4–8 vCPU, 16 GB)
                    ├─ DSIWorkForceWorker (Windows Service)
                    └─ IIS: DSI.WorkForce.Web (HTTPS, app sign-in)
                              │
                    SQL01 (existing SQL Server 2016 SP1+; 2019/2022 recommended)
[Optional] GPU01 with Ollama (if using the local VLM recognizer)
```

**Capacity (measured, local handwriting OCR included).** 20-page test PDF on a 4-core machine:

| Pages processed in parallel | Seconds per page | Sheets per hour |
|---|---|---|
| 1 (one core) | 4.5 | ~800 |
| 4 (one per core, default) | **1.7** | **~2,100** |

The Worker uses one page per CPU core automatically (`MaxDegreeOfParallelism: 0`), also for the pages of a single
big PDF. 5,000 sheets therefore take about 2.5 hours of processing on a 4-core server, or ~1.3 hours on 8 cores.
Since sheets arrive throughout the day (~3.5 per minute on average), there is plenty of headroom; add cores or a
second Worker server (the queue supports several) for peaks. Memory: ~1.5 GB with 4 pages in parallel.

**Pick-up delay.** New uploads and hot-folder files are noticed instantly (file-system events + internal signals),
so processing starts about 2 seconds after the file is complete (`FileStableSeconds`). Polling every
`PollIntervalSeconds` remains as a safety net for network shares. A cloud/VLM recognizer adds its own latency; raise parallelism (or add a second
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
| `Worker:MaxDegreeOfParallelism` | Pages in parallel across all files. `0` = automatic (one per CPU core, max 8). |
| `Worker:MaxMemoryMb` | Memory guard: new pages wait while the Worker uses more than this. `0` = 75% of available memory. |
| `Worker:FileStableSeconds` | A scanned file must be unchanged this long before it is picked up (default 2). |
| `Worker:MaxAttempts` | After this many failures a file is parked as `Failed` (retry from the dashboard). |
| `Extraction:Recognizer` | `Local` (PaddleOCR on CPU, default) / `AzureDocumentIntelligence` / `Ollama` / `None` (manual typing). |
| `Extraction:EscalateTo`, `EscalateBelowConfidence` | Optional second engine for boxes read with low confidence (`None`, `AzureDocumentIntelligence`, `Ollama`; default threshold 0.85). |
| `Extraction:Local:Model / Variants / CpuThreads` | `PPOCRv5` (default) or `EnglishV4`; image variants per box 1–3 (default 3); CPU threads per OCR engine (default 1, fastest with one page per core). |
| `Extraction:Azure:*`, `Extraction:Ollama:*` | Recognizer settings (put keys in environment variables, not in the file). |

Web `appsettings.json`: `Database`, `Upload:HotFolders`. Users and permissions are managed in the app
(*Users & permissions*), the license on the *License* page.

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

* Application sign-in with per-user permissions (salted PBKDF2 password hashes, lockout after 5 failures,
  sessions revoked within a minute on deactivation / permission change). Serve the site over HTTPS only.
* Licensed product: signed (ECDSA) license keys, checked every minute; without a valid key everything is locked
  (see [07-users-permissions-licensing.md](07-users-permissions-licensing.md)). Keep `helao2-license-private.pem` secret.
* The web app and worker use a least-privilege DB role (`dsi_app`); report readers get `dsi_report_reader`
  (read-only on `rpt`).
* Report expressions are validated against a whitelist; identifiers are bracket-quoted; all values are parameters.
* Scans contain personal data (names, EPF numbers): restrict the share permissions, and don't commit scans to git.
