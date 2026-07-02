# WeatherData backups — setup & operations

How to install, configure, and operate the automated `WeatherData` backup (WX-248). Companion to
[`restore-weatherdata.md`](restore-weatherdata.md) (the recovery side). All commands run in
**Windows PowerShell** on the HarderWare box.

## What it does

A Windows **Scheduled Task** ("HarderWare WeatherData Backup") runs `Backup-WeatherData.ps1`
nightly: full backup of `WeatherData` (SQL Express, `SIMPLE` recovery) → `RESTORE VERIFYONLY` →
copy to each configured offsite destination with SHA-256 + size verification → retention prune.
SQL Express has no SQL Agent, so a Scheduled Task drives it (same pattern as `service-watchdog`).
Recovery model is `SIMPLE` / full-only by design — no point-in-time / log backups.

## One-time install

1. **Prerequisites:** SQL Server Express (`.\SQLEXPRESS`) up with `WeatherData`; the offsite
   destination path exists or is creatable (default: Dropbox `C:\Users\PaulH\Dropbox\PH\WxBackups`).

2. **(Optional) edit the config** `tools/Backup-Config.json` before installing — staging dir,
   `RetentionDays`, and the `Destinations` list (see **Configuration** below).

3. **Register the task — elevated** (Run PowerShell as administrator):
   ```powershell
   cd C:\Code\HarderWare\WxServices\tools
   powershell -NoProfile -ExecutionPolicy Bypass -File .\Register-BackupTask.ps1
   ```
   This copies `Backup-WeatherData.ps1` + `Backup-Config.json` into `C:\HarderWare\backup\` (the
   runtime home — so a repo checkout mid-edit never affects the nightly run) and registers the
   SYSTEM task at **daily 08:00 UTC**. Re-runnable; it never overwrites an already-installed config.

4. **Grant the SQL service account write on the staging dir** *if* the first run logs
   `Operating system error 5 (Access is denied)` on the `.bak` path (`BACKUP` runs server-side as
   the SQL service account, not the task account):
   ```powershell
   icacls "C:\HarderWare\backups" /grant "NT Service\MSSQL`$SQLEXPRESS:(OI)(CI)M"
   ```

5. **Smoke-test the automated path:**
   ```powershell
   Start-ScheduledTask -TaskName 'HarderWare WeatherData Backup'
   ```
   Then check `C:\HarderWare\Logs\weatherdata-backup.log` for a `Backup OK` line and confirm a new
   `.bak` in both staging and the Dropbox destination. Full verification incl. the restore drill:
   [`test-procedures/WX-248.md`](test-procedures/WX-248.md).

## Configuration

`Backup-Config.json` (source in `tools/`; the **live** copy the task reads is
`C:\HarderWare\backup\Backup-Config.json` — edit that one to change behavior without a redeploy):

| Field | Meaning |
|---|---|
| `SqlServer` | instance (default `.\SQLEXPRESS`) |
| `Database` | DB name (default `WeatherData`) |
| `LocalStagingDir` | where `BACKUP` writes first (`C:\HarderWare\backups`) |
| `RetentionDays` | prune backups older than this, locally and offsite (default 14) |
| `Differential` | `{ Enabled, EveryHours }` — off by default |
| `Destinations` | list of offsite sinks; each `{ Type, Target, … }` |

**Destinations** are pluggable by `Type`. Only **`file`** is implemented — a Windows path, which
already covers Dropbox / OneDrive / UNC shares (`\\host\share`) / mapped drives. `sftp` / `ftp` /
`s3` are recognized and throw a clear "not implemented" (the seam for later). Multiple destinations
fan out. Remote-transport credentials, when those land, belong in a gitignored overlay — never in
this committed file.

## Where things live

- **Scripts:** source in the repo `tools/`; runtime copies in `C:\HarderWare\backup\`.
- **Backups:** staging `C:\HarderWare\backups`; offsite `C:\Users\PaulH\Dropbox\PH\WxBackups`.
- **Log:** `C:\HarderWare\Logs\weatherdata-backup.log` (UTC). A failure also drops a
  `weatherdata-backup.FAILED` sentinel and exits non-zero (surfaced to log monitoring).
- **File naming:** `WeatherData-{full|diff}-{yyyyMMdd-HHmmss-fff}.{bak|dif}`, timestamp in **UTC**.

## Operations

- **Change destination / retention:** edit `C:\HarderWare\backup\Backup-Config.json` (no redeploy).
- **Run on demand:** `Start-ScheduledTask -TaskName 'HarderWare WeatherData Backup'`, or
  `powershell -File C:\HarderWare\backup\Backup-WeatherData.ps1 -Type Full`.
- **Change schedule:** re-run `Register-BackupTask.ps1` after editing the trigger, or adjust the
  task in Task Scheduler.
- **Recover:** [`restore-weatherdata.md`](restore-weatherdata.md).
