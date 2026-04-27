# Developer Setup Guide

This guide covers everything a developer needs to clone, configure, build, and
run the HarderWare WxServices system on a new machine.

## Prerequisites

Install these before cloning:

| Prerequisite | Version | Notes |
|---|---|---|
| **.NET SDK** | 8.0+ | [Download](https://dotnet.microsoft.com/download/dotnet/8.0). Verify: `dotnet --version` |
| **SQL Server Express** | 2019+ | [Download](https://www.microsoft.com/sql-server/sql-server-downloads). Default instance name `SQLEXPRESS`. Enable TCP/IP in SQL Server Configuration Manager. |
| **WSL** | 2 | `wsl --install` from an elevated prompt. Ubuntu recommended. |
| **wgrib2** | — | Inside WSL: `sudo apt install wgrib2` or build from source. Verify: `wsl wgrib2 --version` |
| **Miniconda** | — | [Download](https://docs.conda.io/en/latest/miniconda.html). Create the wxvis environment (see below). |
| **Docker Desktop** | — | [Download](https://www.docker.com/products/docker-desktop/). Required for the observability stack (Prometheus + Grafana). |

### Create the wxvis conda environment

```bash
conda create -n wxvis python=3.11 -y
conda activate wxvis
pip install -r WxServices\src\WxVis\requirements.txt
```

Note the full path to the environment's `python.exe` — you'll need it for
configuration (e.g. `C:\Users\<you>\miniconda3\envs\wxvis\python.exe`).

## Clone and configure

```bash
git clone <repo-url>
cd HarderWare\WxServices
```

### Edit `appsettings.shared.json`

This is the single source of truth for all runtime configuration.  It is
git-tracked, so keep secrets out of it — use `appsettings.local.json` for those
(see below).

Settings you **must** change for your machine:

| Setting | Example | Notes |
|---|---|---|
| `InstallRoot` | `C:\HarderWare` | Where services, logs, plots, and scripts are deployed. Change if you want a different location. |
| `Fetch:HomeIcao` | `KDWH` | ICAO code of your nearest METAR station |
| `Fetch:HomeLatitude` | `30.07` | Decimal degrees |
| `Fetch:HomeLongitude` | `-95.55` | Decimal degrees (negative = west) |
| `WxVis:CondaPythonExe` | `C:\Users\<you>\miniconda3\envs\wxvis\python.exe` | Full path to the wxvis conda environment Python executable |

Settings with sensible defaults you may want to review:

| Setting | Default | Notes |
|---|---|---|
| `ConnectionStrings:WeatherData` | `Server=.\SQLEXPRESS;...` | Change if your SQL Server instance name differs |
| `Fetch:BoundingBoxDegrees` | `9` | Radius in degrees around home location for METAR/GFS data |
| `Gfs:Wgrib2WslPath` | `/usr/local/bin/wgrib2` | Path to wgrib2 inside WSL |
| `WxVis:SynopticMapArgs` | `--extent south_central` | Map extent for synoptic analysis renders |

### Create `appsettings.local.json` for secrets

Create this file in the **solution root** (next to `appsettings.shared.json`).
It is git-ignored and holds credentials:

```json
{
  "Smtp": {
    "Username": "your-email@gmail.com",
    "Password": "your-gmail-app-password",
    "FromAddress": "your-email@gmail.com"
  },
  "Claude": {
    "ApiKey": "sk-ant-..."
  },
  "What3Words": {
    "ApiKey": "your-w3w-api-key"
  },
  "Report": {
    "Recipients": [
      {
        "Id": "dev",
        "Email": "your-email@gmail.com",
        "Name": "Your Name",
        "Address": "123 Main St, City, State ZIP"
      }
    ]
  }
}
```

The deploy script copies this file into each service's publish directory
automatically.

### Create the database

The database is created automatically on first service startup via
`EnsureCreatedAsync`.  No manual SQL scripts are needed.  Just ensure SQL
Server Express is running and the connection string is correct.

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

## Deploy

The deploy script must be run from an **elevated** (Administrator) PowerShell
prompt because it manages Windows services.

```powershell
.\Deploy-WxService.ps1 all
```

This:
1. Copies WxVis Python scripts to `{InstallRoot}\WxVis\`
2. Publishes and restarts all four Windows services
3. Publishes WxManager and WxViewer

The script reads `InstallRoot` from `appsettings.shared.json` and uses
`$PSScriptRoot` as the solution root — no hardcoded paths to edit.

### Individual targets

```powershell
.\Deploy-WxService.ps1 WxParserSvc    # Single service
.\Deploy-WxService.ps1 WxManager      # Management GUI only
.\Deploy-WxService.ps1 WxViewer       # Desktop viewer only
.\Deploy-WxService.ps1 WxVis          # Python scripts only
```

Valid names: `WxParserSvc`, `WxReportSvc`, `WxMonitorSvc`, `WxVisSvc`,
`WxViewer`, `WxManager`, `WxVis`, `all`.

## First-time service registration

Before the first deploy, register the Windows services (elevated prompt):

```powershell
$ir = "C:\HarderWare"   # or your InstallRoot

sc.exe create WxParserSvc  binPath= "$ir\BuildCache\WxServices\WxParser.Svc\bin\Release\net8.0\publish\WxParser.Svc.exe"
sc.exe create WxReportSvc  binPath= "$ir\BuildCache\WxServices\WxReport.Svc\bin\Release\net8.0\publish\WxReport.Svc.exe"
sc.exe create WxMonitorSvc binPath= "$ir\BuildCache\WxServices\WxMonitor.Svc\bin\Release\net8.0\publish\WxMonitor.Svc.exe"
sc.exe create WxVisSvc     binPath= "$ir\BuildCache\WxServices\WxVis.Svc\bin\Release\net8.0\publish\WxVis.Svc.exe"
```

The deploy script updates the `binPath` on each subsequent deploy, so
registration is a one-time step.

## Start the observability stack

```bash
cd observability
docker compose up -d
```

This starts Prometheus (port 9090) and Grafana (port 3000, admin/grafana).
The WxParser dashboard is provisioned automatically.

## Configuration layering

Configuration is loaded in this order (last wins):

1. `appsettings.shared.json` — git-tracked, single source of truth
2. `appsettings.json` — per-service settings (intervals, timeouts)
3. `{InstallRoot}\appsettings.local.json` — machine-wide overrides
4. `appsettings.local.json` — per-service overrides (copied from source by deploy script)

All paths (logs, plots, temp, scripts) are derived from `InstallRoot` at
runtime via the `WxPaths` class in `WxServices.Common`.

## Project structure

```
WxServices/
├── appsettings.shared.json      ← single config file (git-tracked)
├── log4net.shared.config        ← single log config (git-tracked)
├── Deploy-WxService.ps1         ← developer deploy script
├── DESIGN.md                    ← architecture documentation
├── DEVELOPER-README.md          ← this file
└── src/
    ├── MetarParser/             ← METAR text parser library
    ├── TafParser/               ← TAF text parser library
    ├── GribParser/              ← wgrib2 subprocess wrapper
    ├── MetarParser.Data/        ← EF Core entities, fetchers, DB context
    ├── WxServices.Logging/      ← log4net wrapper (Logger class)
    ├── WxServices.Common/       ← shared utilities (WxPaths, SmtpSender)
    ├── WxInterp/                ← METAR+TAF+GFS → WeatherSnapshot
    ├── WxParser.Svc/            ← Windows service: METAR/TAF + GFS fetch
    ├── WxReport.Svc/            ← Windows service: Claude reports + email
    ├── WxMonitor.Svc/           ← Windows service: log/heartbeat monitoring
    ├── WxVis.Svc/               ← Windows service: map rendering
    ├── WxViewer/                ← WPF desktop app: weather map viewer
    ├── WxManager/               ← WPF management GUI
    └── WxVis/                   ← Python visualisation scripts
```

## Startup order

Start `WxParserSvc` first and allow at least one fetch cycle (~10 minutes)
before starting `WxReportSvc`, so METAR data is available for station
resolution.  The deploy script handles this by deploying services in the
correct order.

## Troubleshooting

| Symptom | Check |
|---|---|
| No METAR data | Is `Fetch:HomeIcao` set? Is `Fetch:HomeLatitude`/`HomeLongitude` correct? Check `wxparser-svc.log`. |
| No reports sent | Are SMTP credentials in `appsettings.local.json`? Is `Claude:ApiKey` set? Check `wxreport-svc.log`. |
| Maps not rendering | Is `WxVis:CondaPythonExe` correct? Are Python scripts in `{InstallRoot}\WxVis\`? Check `wxvis-svc.log`. |
| GFS fetch fails | Is WSL running? Is wgrib2 installed? Check `wxparser-svc.log` for GFS errors. |
| Service won't start | Run `sc.exe query <ServiceName>` to check registration. Check logs in `{InstallRoot}\Logs\`. |

All logs are in `{InstallRoot}\Logs\` with UTC timestamps.

## Jira labels

Issues in the Jira `WX` project are tagged with labels across three orthogonal
dimensions.  A typical issue carries one to three labels: usually one
component + one work-character, occasionally a meta label.

The authoritative label registry lives in Jira as **WX-37 "Label taxonomy
reference"**.  That issue exists solely to keep every approved label in Jira's
autocomplete pool (Jira Cloud has no admin page for labels — an unused label
silently disappears from autocomplete).  Do not close WX-37 or strip its
labels.

### Component / area

`wxparser`, `wxreport`, `wxmonitor`, `wxvis`, `wxmanager`, `wxviewer`,
`database`, `claude-integration`, `config`, `infrastructure`

### Work character

`ui`, `reliability`, `observability`, `performance`, `security`, `refactor`,
`tech-debt`, `docs`, `quick-win`

### Source / meta

`coderabbit`, `ai-collab`, `needs-design`, `incident`

### Conventions

- Lowercase, hyphen-separated (`wxmanager`, not `WxManager` or `wx_manager`).
- Prefer reuse over invention.  Check the list above before adding a new one.
- If a new label is genuinely needed, add it to WX-37 *and* to this section in
  the same commit.
