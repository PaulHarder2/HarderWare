# Developer Setup Guide

This guide covers everything a developer needs to clone, configure, build, and
run the HarderWare WxServices system on a new machine.

The four headless services (WxParser, WxReport, WxMonitor, WxVis) run as **Docker
containers** built from this repository.  The two desktop applications (WxManager,
WxViewer) run natively on Windows.  First-time configuration is done by a setup
console that ships with the solution — you should not need to hand-write any
configuration file.

## Prerequisites

Install these before cloning:

| Prerequisite | Version | Notes |
|---|---|---|
| **.NET SDK** | 8.0+ | [Download](https://dotnet.microsoft.com/download/dotnet/8.0). Verify: `dotnet --version` |
| **SQL Server Express** | 2019+ | [Download](https://www.microsoft.com/sql-server/sql-server-downloads). Default instance name `SQLEXPRESS`. Enable **TCP/IP** in SQL Server Configuration Manager, and enable **Mixed Mode** authentication — the containers connect over TCP as a SQL login, which Windows-only auth cannot serve. |
| **Docker Desktop** | — | [Download](https://www.docker.com/products/docker-desktop/). Required — the four services run as containers, and it also hosts the optional observability stack. |

Native `wgrib2` and a Miniconda environment are **not** prerequisites.  The
WxParser image bundles a Linux `wgrib2` build, and the WxVis image carries its
own Python rendering stack, so neither tool needs to exist on the host.

## Clone and configure

```bash
git clone <repo-url>
cd HarderWare\WxServices
```

### Activate the git hooks (one-time, per clone)

The repo ships a tracked `pre-push` hook that blocks a push when
`Directory.Build.props` and `VERSIONS.md` disagree on the current version. Point
git at the tracked hooks directory once after cloning (from anywhere in the working tree — git resolves the relative path from the repo root):

```bash
git config core.hooksPath WxServices/tools/hooks
```

This guards against the class of mistake where a `VERSIONS.md` row is added but
the `<Version>` bump is forgotten (or vice-versa). CI runs the same check as a
non-bypassable backstop, so the hook is a fast local fail, not the only gate. In
a genuine emergency you can skip it with `git push --no-verify`.

### Run the setup console

First-time configuration is a script, not a checklist.  From the `WxServices`
directory:

```powershell
dotnet run --project src\WxServices.Setup
```

The defaults suit a normal developer machine. To pass any of the flags below,
put them **after a bare `--`** — everything before it belongs to the .NET CLI,
everything after it is handed to the setup console:

```powershell
dotnet run --project src\WxServices.Setup -- --mode full --server .\SQLEXPRESS
```

Without that separator the CLI tries to interpret `--mode` as one of its own
options and the run fails before the console starts.

It runs in this order, and changes nothing until you confirm:

1. **Prerequisite gate — detects, never fixes.**  It checks that SQL Server is
   reachable with your Windows credentials, that Mixed Mode authentication is
   enabled, that you are a SQL `sysadmin`, that SQL Server is listening on TCP,
   and that the repository's `services` directory is present.  Any failure stops
   the run with nothing changed.  Docker is checked too, but only warns — it is
   needed to *run* the services, not to run this script.
2. **Prompts for the foundational location values** — home airport ICAO code,
   latitude, longitude, fetch bounding-box size, the region bounds, and the map
   extent — validating each as you type.
3. **Prompts for a password** for the SQL login the containers will use
   (`wxservices` by default).  Input is not echoed.
4. **Shows you the exact SQL it intends to run** and waits for a `yes`.
5. **Provisions**: creates the login, creates the database and applies all
   migrations, then maps the login into the database with least-privilege roles.
6. **Writes five `appsettings.local.json` files** — one per service container,
   generated from the committed `.example` templates, plus one machine-wide file
   in your `InstallRoot`.
7. **Seeds the foundational settings** into the database's `Config` table.

The run is **idempotent**: if it fails partway, fix the cause and run it again.
Nothing in it elevates — host-level changes that need administrator rights (such
as enabling Mixed Mode or TCP) are reported for you to perform, never performed
for you.

Useful flags — every destination is an input, with no hardcoded targets (pass
them after the `--` separator shown above):

| Flag | Default | Purpose |
|---|---|---|
| `--mode` | `full` | `full` is the from-source developer path. `opregion` is recognised but its runtime-only mechanics are not implemented yet. |
| `--install-root` | `C:\HarderWare` (or `WXSERVICES_INSTALL_ROOT`) | Where logs, plots, and the machine-wide config file go |
| `--services-dir` | the repo's `services\` | Where the per-container config files are written |
| `--database` | `WeatherData` | Database name |
| `--sql-login` | `wxservices` | Login the containers authenticate as |
| `--server` | `.\SQLEXPRESS` | Target SQL Server instance |

### Secrets

Do **not** put secrets in a configuration file.  The SMTP password, Claude API
key, and What3Words key live in the database, and the way to enter them is
**WxManager → Configure tab**.  They are never written to disk.

The one unavoidable exception is the database connection string itself, which
cannot live in the store it unlocks.

### Changing a foundational setting afterwards

The values the console prompts for — home ICAO, latitude, longitude,
`Fetch:BoundingBoxDegrees`, the region bounds, and the map extent — are seeded
into the database's `Config` table, not into a file you can edit. To change one
later, either re-run the setup console (it is idempotent and will update the
existing rows) or edit the `Config` row directly. They are deliberately absent
from WxManager's Configure tab: the rest of the system derives from them, so
they are not day-to-day settings.

## Build

```powershell
dotnet build WxServices.sln
```

## Code style

C# style rules are defined across two files at the solution root:

- **`WxServices/.editorconfig`** — the unmodified output of
  `dotnet new editorconfig` (Microsoft default ruleset). Read automatically by
  Visual Studio, JetBrains Rider, VS Code, and `dotnet format`.
- **`WxServices/.globalconfig`** — project-wide severity overrides that elevate
  specific IDE rules from "suggestion" to "warning" so they fire on
  `dotnet build`. Currently elevated:
  - `IDE0055` (Fix formatting): trailing whitespace, brace placement,
    indentation, etc.

`<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>` in
`Directory.Build.props` makes every `dotnet build` run the IDE analyzers and
report violations of any elevated rule.

To apply the rules to the solution: `dotnet format WxServices.sln`.

To add a new rule to the build-time enforced set: append a
`dotnet_diagnostic.IDExxxx.severity = warning` line to
`WxServices/.globalconfig`. Elevating a rule can surface latent violations
against existing code that need cleanup in the same PR.

## Continuous integration

`.github/workflows/ci.yml` runs on every pull request (open, push, reopen)
and on every push to `master`. It also has a manual trigger
(`workflow_dispatch`) for one-off re-runs from the Actions tab.

**What runs**, in order, on `ubuntu-latest` against `.NET 8.0.x`:

1. **Restore** — `dotnet restore WxServices.CI.slnf`
2. **Build** — `dotnet build WxServices.CI.slnf -c Release --no-restore`
3. **Test** — `dotnet test WxServices.CI.slnf -c Release --no-build`
4. **Format check** — `dotnet format WxServices.CI.slnf --verify-no-changes`
5. **Linux-publish smoke** — `dotnet publish` for each of the four
   headless services (`WxParser.Svc`, `WxReport.Svc`, `WxMonitor.Svc`,
   `WxVis.Svc`) with `-r linux-x64 --self-contained false`. Catches
   case-sensitive path regressions and missing references that would
   surface only on a non-Windows host.

**Solution filter scope.** CI uses `WxServices.CI.slnf`, which lists the
cross-platform projects (`net8.0` TFM). The two WPF projects
(`WxManager`, `WxViewer`, both `net8.0-windows`) are intentionally
excluded — they cannot build on a Linux runner without targeting-pack
hacks, and there is no scenario in which a Linux-built WPF assembly is
useful. WPF projects are gate-checked locally via `dotnet build` on the
developer's Windows machine before deploy.

**Reading a failure.** Open the PR's *Checks* tab on GitHub. Click the
failing CI run, expand the failed step, and read the log. The most
common failure modes:

| Failure | Where it surfaces | Local fix |
|---|---|---|
| Build error | Build step | `dotnet build WxServices.CI.slnf -c Release` |
| Test failure | Test step | `dotnet test WxServices.CI.slnf -c Release` |
| Style/whitespace drift | Format check | `dotnet format WxServices.sln` (full sln; safe even though CI uses the filter) |
| Linux-publish error | Publish step | `dotnet publish src/Wx*.Svc/Wx*.Svc.csproj -c Release -r linux-x64 --self-contained false` |

**Re-running a stuck or failed workflow.** From the Actions tab,
select the workflow run, then click **Re-run jobs → Re-run all jobs**
or **Re-run failed jobs**. Concurrency control (`cancel-in-progress: true`)
means a new push to the same PR will automatically cancel any in-flight
run on that ref and start a fresh one — so often the simplest "re-run"
is just an empty `git commit --allow-empty -m "ci kick" && git push`.

**Caching.** `actions/setup-dotnet@v4` caches the NuGet package store
keyed off all `*.csproj` files. After the first run on a given branch,
restore drops from ~30 s to a few seconds.

**Adding a new project.** A new cross-platform project (`net8.0`) must
be added to `WxServices.CI.slnf` to participate in CI. New WPF projects
should *not* be added — extend the local-Windows-only verification path
instead.

**Branch protection.** `master` is protected: every change must arrive via
a pull request, and the merge button stays disabled until the
`Build, test, format-check, Linux-publish smoke` check goes green. The
"Do not allow bypassing" setting is on — even the repo owner cannot
override the gate. PRs must also be up-to-date with `master` before
merging, so semantic conflicts between concurrent PRs surface as a
required rebase rather than a broken `master`. The protection rule
lives at *Settings → Branches → Branch protection rules → master* in
the GitHub UI; if you need to adjust the required check name (e.g.
after renaming the workflow or its job), update it there.

## Deploy

The deploy script must be run from an **elevated** (Administrator) PowerShell
prompt.

```powershell
.\Deploy-WxService.ps1 all
```

This:
1. Builds and starts the four service containers via
   `services/docker-compose.yml`, pointing their bind mounts at the resolved
   host `InstallRoot` subdirectories, and verifies each container is running and
   has logged its start-up banner before moving on
2. Brings up the autoheal sidecar
3. Publishes WxManager and WxViewer
4. Exits non-zero if anything failed to come up

The script reads `InstallRoot` from `appsettings.shared.json` and uses
`$PSScriptRoot` as the solution root — no hardcoded paths to edit.

Because the service binaries are baked into their images, deploying a service
means rebuilding and recreating its container — there is no publish-to-folder
step and no Windows service to stop and restart.

### Deploy-history log

Each deployed app appends one line to `{InstallRoot}\Logs\deploy-history.log`
recording the UTC time, product version, and git short SHA — using the same
timestamp format as the services' `wx*-svc.log` files, so the deploy timeline
reads alongside them:

```text
2026-06-03 19:50:00.000 INFO  [Deploy] WxReportSvc   1.12.0   75e04ca  OK
```

Console/GUI apps are logged once their push settles; a containerized service is
logged `OK` only after it verifies as running, and `FAIL` if it starts but does
not stay up (so a crash-on-init never reads as a successful deploy). Logging is
best-effort and never aborts a deploy.

### Individual targets

```powershell
.\Deploy-WxService.ps1 WxParserSvc    # Single service container
.\Deploy-WxService.ps1 WxManager      # Management GUI only
.\Deploy-WxService.ps1 WxViewer       # Desktop viewer only
.\Deploy-WxService.ps1 autoheal       # The autoheal sidecar only
```

Valid names: `WxParserSvc`, `WxReportSvc`, `WxMonitorSvc`, `WxVisSvc`,
`WxViewer`, `WxManager`, `autoheal`, `all`.

### Running the containers directly

The deploy script is a convenience wrapper. You can drive Compose yourself from
the repository's `services` directory:

```bash
docker compose up -d          # start (build first if needed)
docker compose up -d --build  # rebuild after a code change
docker compose ps             # what is running
docker compose logs -f wxreport
docker compose stop           # stop the containers, keep them
docker compose down           # stop AND remove the containers
```

`stop` and `down` are not interchangeable, and the difference matters after a
reboot — see the runbook below before using either in anger.

Each service reads its bind-mounted `appsettings.local.json` from
`services/<service>/`, which the setup console generated. Those files are
gitignored; only the `.example` templates are committed.

**The full operational story lives in
`docs/policies-and-procedures/container-stack-operations.md`** — restart
policy, reboot recovery, the autoheal sidecar reconcile, and the
hand-stopped-stays-stopped semantic. Treat that runbook as authoritative for
day-to-day operation; the commands above are only enough to get you moving.

## Start the observability stack

Before first launch on a fresh clone, create `observability/.env` with the
Grafana admin password:

```bash
echo "GRAFANA_ADMIN_PASSWORD=<value-from-RoboForm>" > observability/.env
```

The file is gitignored. Without it, `docker compose up` fails fast
with a named error rather than booting Grafana with a weak default. The
source of truth for the password is the Grafana entry in RoboForm.

```bash
cd observability
docker compose up -d
```

This starts Prometheus (port 9090) and Grafana (port 3000, admin / value
from `.env`). The WxParser dashboard is provisioned automatically.

## Configuration layering

The **database is the runtime source of truth** for configuration. The JSON
files still exist and still load, but the database `Config` table is applied
last and wins:

1. `appsettings.shared.json` — git-tracked defaults
2. `appsettings.json` — per-service settings (intervals, timeouts)
3. `{InstallRoot}\appsettings.local.json` — machine-wide overrides
4. `appsettings.local.json` — per-service overrides (bind-mounted into each container)
5. **the `Config` table in the database** — last wins

A small set of **bootstrap-critical** keys is deliberately excluded from the
database layer and read only from files:

| Key prefix | Why it must stay file-sourced |
|---|---|
| `ConnectionStrings:` | It is what opens the database — it cannot live inside it |
| `Database:StartupRetry:` | Governs the retry loop that opens the database |
| `Telemetry:` | Read during host construction, before configuration is available |
| `Claude:TimeoutSeconds` | Applied when the HTTP client is constructed at start-up |

The same list is enforced on the write side: the Configure tab and the setup
console both refuse to write a bootstrap-critical key into the database, using
one shared definition rather than a re-derived copy.

All paths (logs, plots, temp, scripts) are derived from `InstallRoot` at
runtime via the `WxPaths` class in `WxServices.Common`.

## Project structure

```
WxServices/
├── appsettings.shared.json      ← git-tracked config defaults
├── log4net.shared.config        ← single log config (git-tracked)
├── Deploy-WxService.ps1         ← developer deploy script
├── DESIGN.md                    ← architecture documentation
├── DEVELOPER-README.md          ← this file
├── tools/wgrib2-linux/          ← Linux wgrib2 binary baked into the WxParser image
└── src/
    ├── MetarParser/             ← METAR text parser library
    ├── TafParser/               ← TAF text parser library
    ├── GribParser/              ← wgrib2 subprocess wrapper
    ├── MetarParser.Data/        ← EF Core entities, fetchers, DB context
    ├── WxServices.Logging/      ← log4net wrapper (Logger class)
    ├── WxServices.Common/       ← shared utilities (WxPaths, SmtpSender)
    ├── WxServices.Setup/        ← first-time setup console
    ├── WxInterp/                ← METAR+TAF+GFS → WeatherSnapshot
    ├── WxParser.Svc/            ← containerized service: METAR/TAF + GFS fetch
    ├── WxReport.Svc/            ← containerized service: Claude reports + email
    ├── WxMonitor.Svc/           ← containerized service: log/heartbeat monitoring
    ├── WxVis.Svc/               ← containerized service: map rendering
    ├── WxViewer/                ← WPF desktop app: weather map viewer
    ├── WxManager/               ← WPF management GUI
    └── WxVis/                   ← Python visualisation scripts (baked into the WxVis image)
```

The container build definitions live at the repository root, outside this
directory: `services/docker-compose.yml` plus one `Dockerfile` per service, and
`observability/` for the metrics stack. Their build context is the repository
root, so both trees are in scope when an image is built.

## Startup order

The services coordinate through the database rather than by calling each other,
so they can start in any order. WxReport simply produces nothing until WxParser
has completed a fetch cycle — allow roughly 10 minutes after a cold start
before expecting a report.

## Troubleshooting

| Symptom | Check |
|---|---|
| Containers won't start | Is Docker Desktop running? Have the images been built (`docker compose up -d --build`)? |
| Container starts, then exits | `docker compose logs <service>` — a missing bind-mounted `appsettings.local.json` is the usual cause; re-run the setup console. |
| Cannot reach the database from a container | Is SQL Server TCP/IP enabled and Mixed Mode on? Is the `wxservices` login's password correct in the bind-mounted config? |
| No METAR data | Were the home location values seeded? Check the `Config` table. Check `wxparser-svc.log`. |
| No reports sent | Are the SMTP credentials and Claude API key entered on WxManager's Configure tab? Check `wxreport-svc.log`. |
| Maps not rendering | Check `wxvis-svc.log`. The Python stack is inside the image, so this is not a host Python problem. |
| Logs empty or stale on the host | The log bind mount is stale — `docker compose up -d --force-recreate`. |

All logs are in `{InstallRoot}\Logs\` with UTC timestamps — the containers write
them to the host through a bind mount.
