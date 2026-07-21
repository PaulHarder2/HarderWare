# Restoring the WeatherData database

Recovery runbook for the `WeatherData` SQL Server (Express) database from a backup produced
by `tools/Backup-WeatherData.ps1` (WX-248). Setup & operations of the backup itself:
[`backup-weatherdata.md`](backup-weatherdata.md). **A backup you have never restored is not a
backup** — the WX-248 acceptance drill (below) proves this path works; this runbook is what
you follow when it's for real.

Run everything in **Windows PowerShell / `sqlcmd`** on the HarderWare box. `sqlcmd` needs
`-E -C` for the local instance (Windows auth + trust-server-certificate on ODBC 18+).

## 1. Find a backup

Backups are `WeatherData-full-<UTCstamp>.bak` (and `-diff-` differentials, if enabled) in the
staging dir and every configured destination:

- Local staging: `C:\HarderWare\backups`
- Offsite (Dropbox): `C:\Users\PaulH\Dropbox\PH\WxBackups`

Pick the newest full backup (and, if differentials are in use, the newest differential taken
*after* it). Prefer the offsite copy if the local disk is the thing that failed.

## 2. Verify the file before trusting it

```powershell
sqlcmd -S .\SQLEXPRESS -E -C -b -Q "RESTORE VERIFYONLY FROM DISK = N'<path-to.bak>' WITH CHECKSUM;"
```
Expect `The backup set on file 1 is valid.` and exit code 0.

## 3a. Restore into a scratch DB (safe — the default, non-destructive)

This is what the acceptance drill does. It does **not** touch the live `WeatherData`.

```powershell
sqlcmd -S .\SQLEXPRESS -E -C -b -Q @"
RESTORE DATABASE [WeatherData_restoretest]
  FROM DISK = N'<path-to.bak>'
  WITH MOVE 'WeatherData'     TO N'C:\HarderWare\backups\WeatherData_restoretest.mdf',
       MOVE 'WeatherData_log' TO N'C:\HarderWare\backups\WeatherData_restoretest_ldf.ldf',
       RECOVERY, REPLACE;
"@
```

> The logical file names (`WeatherData`, `WeatherData_log`) come from
> `RESTORE FILELISTONLY FROM DISK = N'<path>'` — run that first if they differ.

Sanity-check row counts, then drop the scratch DB when done:
```powershell
sqlcmd -S .\SQLEXPRESS -E -C -Q "SELECT COUNT(*) AS LanguageTemplates FROM WeatherData_restoretest.dbo.LanguageTemplates;"
sqlcmd -S .\SQLEXPRESS -E -C -Q "DROP DATABASE [WeatherData_restoretest];"
```

## 3b. Restore OVER the live database (destructive — real recovery only)

Only when `WeatherData` is actually lost/corrupt. **Stop the four service containers first** so
nothing holds a connection, then restore with `REPLACE`.

> **The services are containers, not Windows services.** Earlier revisions of this runbook used
> `Stop-Service WxParserSvc, … -ErrorAction SilentlyContinue`. That command now **silently succeeds
> while doing nothing** — the native services were deleted at the containerization cutover and
> WX-329 removed native-service support outright — and the suppressed errors hide it. The containers
> would stay up, reconnect under `restart: unless-stopped` plus the startup-retry loop, and race the
> `SET SINGLE_USER` below: the restore then fails with *"exclusive access could not be obtained"*, or
> services write into a half-restored database. Also close **WxManager** and **WxViewer** — they run
> natively and hold their own connections.

```powershell
# 1. stop the service containers (from the repo's services\ directory)
docker compose stop
docker compose ps          # confirm all four are stopped before continuing

# 2. restore (single-user to force-close stray connections)
sqlcmd -S .\SQLEXPRESS -E -C -b -Q @"
ALTER DATABASE [WeatherData] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
RESTORE DATABASE [WeatherData] FROM DISK = N'<path-to-full.bak>' WITH REPLACE, NORECOVERY;
-- if applying a later differential, restore it next; otherwise skip straight to RECOVERY:
RESTORE DATABASE [WeatherData] FROM DISK = N'<path-to.dif>' WITH RECOVERY;
ALTER DATABASE [WeatherData] SET MULTI_USER;
"@
```
(If there is no differential, use `WITH REPLACE, RECOVERY` on the full and omit the `.dif` line.)

```powershell
# 3. restart the containers (from services\)
docker compose start
docker compose ps          # all four running; healthchecks go healthy within a few minutes
```

## 4. After a real restore

- Confirm the services came up and a report cycle runs clean (`C:\HarderWare\Logs\wxreport-svc.log`).
- Curated vocabulary (`LanguageTemplates`) and `GlobalSettings` (API keys, SMTP) are the
  irreplaceable data this protects — spot-check they're present.

## Notes

- The SQL Server service account must be able to write `LocalStagingDir` (BACKUP is
  server-side). See `Register-BackupTask.ps1` if the first backup logs *Access is denied*.
- Recovery model is `SIMPLE` — full (+ optional differential) only, no transaction-log /
  point-in-time recovery by design (WX-248).
