# HarderWare WxServices — Installation Guide

This guide walks through installing the HarderWare weather services system
on a fresh Windows machine.  The system fetches weather data (METAR, TAF,
GFS forecasts), generates natural-language weather reports via Claude AI,
renders weather maps, and emails personalised reports to subscribers.

## 1. System Requirements

- Windows 10 or 11 (64-bit)
- 8 GB RAM minimum (16 GB recommended — SQL Server and conda share memory)
- 10 GB free disk space
- Internet connection (for weather data feeds and Claude API)

## 2. Install Prerequisites

### 2.1 .NET 8 Runtime

Download and install the **.NET 8 Runtime** (not the SDK, unless you plan
to develop):

> https://dotnet.microsoft.com/download/dotnet/8.0

Verify: open a command prompt and run `dotnet --info`.

### 2.2 SQL Server Express

Download and install **SQL Server Express** (free):

> https://www.microsoft.com/sql-server/sql-server-downloads

During setup:
- Use the default instance name `SQLEXPRESS`.
- Enable **Windows Authentication** (the default).

After installation:
1. Open **SQL Server Configuration Manager**.
2. Under *SQL Server Network Configuration → Protocols for SQLEXPRESS*,
   ensure **TCP/IP** is enabled.
3. Restart the SQL Server service.

The database itself (`WeatherData`) is created automatically on first
service startup — no manual SQL scripts are needed.

### 2.3 WSL and wgrib2

WSL (Windows Subsystem for Linux) is required for GFS forecast data
processing.

1. Open an **elevated** command prompt and run:
   ```
   wsl --install
   ```
   Reboot if prompted.  Ubuntu is installed by default.

2. Open a WSL terminal and install wgrib2:
   ```bash
   sudo apt update
   sudo apt install wgrib2
   ```
   If `wgrib2` is not in your distribution's package manager, build from
   source: https://www.cpc.ncep.noaa.gov/products/wesley/wgrib2/

3. Verify: `wsl wgrib2 --version`

### 2.4 Miniconda and the wxvis Environment

Miniconda provides the Python environment for weather map rendering.

1. Download and install Miniconda:
   > https://docs.conda.io/en/latest/miniconda.html

2. Open the **Anaconda Prompt** and create the wxvis environment:
   ```
   conda create -n wxvis python=3.11 -y
   conda activate wxvis
   pip install -r C:\HarderWare\WxVis\requirements.txt
   ```
   (Adjust the path if you chose a different install root.)

3. Note the full path to the environment's `python.exe`, e.g.:
   ```
   C:\Users\<YourName>\miniconda3\envs\wxvis\python.exe
   ```
   You will need this for configuration.

### 2.5 Docker Desktop (Optional — for Observability)

Docker is needed only if you want the Prometheus + Grafana monitoring
dashboard.

1. Download and install Docker Desktop:
   > https://www.docker.com/products/docker-desktop/

2. Verify: open a command prompt and run `docker info`.

## 3. Install the Product

Extract the distribution archive into your chosen installation directory.
The default is `C:\HarderWare`.  The directory structure should be:

```
C:\HarderWare\
├── appsettings.shared.json
├── log4net.shared.config
├── Logs\
├── plots\
├── temp\
├── BuildCache\WxServices\
│   ├── WxParser.Svc\...\publish\
│   ├── WxReport.Svc\...\publish\
│   ├── WxMonitor.Svc\...\publish\
│   └── WxVis.Svc\...\publish\
├── WxManager\
├── WxViewer\
└── WxVis\
```

If you use a directory other than `C:\HarderWare`, update the `InstallRoot`
setting in `appsettings.shared.json` (see below).

## 4. Configure

The easiest way to configure the system is to use **WxManager → Configure
tab**, which provides a graphical editor with test buttons for database
connectivity, SMTP, and the Claude API.

Alternatively, you can edit `appsettings.shared.json` in the install root
directly.  The Configure tab writes non-secret settings to
`appsettings.local.json` (an override layer) and stores credentials in
the database.

### Required settings

| Setting | Description | Example |
|---|---|---|
| `InstallRoot` | Installation directory (change only if not `C:\HarderWare`) | `C:\\HarderWare` |
| `Fetch:HomeIcao` | ICAO code of your nearest METAR station | `KDWH` |
| `Fetch:HomeLatitude` | Your latitude in decimal degrees | `30.07` |
| `Fetch:HomeLongitude` | Your longitude in decimal degrees (negative = west) | `-95.55` |
| `WxVis:CondaPythonExe` | Full path to the wxvis conda `python.exe` | `C:\\Users\\You\\miniconda3\\envs\\wxvis\\python.exe` |

To find your nearest METAR station, search for your location at:
> https://aviationweather.gov/data/metar/

### Secrets (SMTP Credentials and Claude API Key)

Secrets are stored in the database, not in configuration files.  Use
**WxManager → Configure tab** to enter:

- **SMTP Username** — your Gmail address
- **SMTP Password** — a Gmail App Password (not your regular password)
- **SMTP From Address** — typically the same Gmail address
- **Claude API Key** — begins with `sk-ant-...`

Click **Save** in the Configure tab and the credentials are written
directly to the database.  They never appear in any file on disk.

**Gmail App Password:** Go to https://myaccount.google.com/apppasswords
to generate an app-specific password (requires 2-factor authentication).

**Claude API Key:** Sign up at https://console.anthropic.com/ and create
an API key.

### Optional settings

| Setting | Default | Description |
|---|---|---|
| `ConnectionStrings:WeatherData` | `Server=.\SQLEXPRESS;...` | Change if your SQL Server instance differs |
| `Fetch:BoundingBoxDegrees` | `9` | Radius around home location for data collection |
| `Gfs:Wgrib2WslPath` | `/usr/local/bin/wgrib2` | Path to wgrib2 inside WSL |
| `WxVis:MapExtent` | `south_central` | Map extent: preset name or W,E,S,N coordinates |
| `Monitor:AlertEmail` | (empty) | Email address for service health alerts |

## 5. Register Windows Services

Open an **elevated** PowerShell prompt and register the four services.
Adjust the path if your `InstallRoot` is not `C:\HarderWare`:

```powershell
$ir = "C:\HarderWare"

sc.exe create WxParserSvc  binPath= "$ir\BuildCache\WxServices\WxParser.Svc\bin\Release\net8.0\publish\WxParser.Svc.exe"
sc.exe create WxReportSvc  binPath= "$ir\BuildCache\WxServices\WxReport.Svc\bin\Release\net8.0\publish\WxReport.Svc.exe"
sc.exe create WxMonitorSvc binPath= "$ir\BuildCache\WxServices\WxMonitor.Svc\bin\Release\net8.0\publish\WxMonitor.Svc.exe"
sc.exe create WxVisSvc     binPath= "$ir\BuildCache\WxServices\WxVis.Svc\bin\Release\net8.0\publish\WxVis.Svc.exe"
```

## 6. Start Services

```powershell
sc.exe start WxParserSvc
```

Wait ~10 minutes for the first METAR fetch cycle to complete, then:

```powershell
sc.exe start WxReportSvc
sc.exe start WxMonitorSvc
sc.exe start WxVisSvc
```

**Startup order matters:** WxParserSvc must run first so weather data is
available when WxReportSvc starts.

## 7. Add Recipients

Open **WxManager** (`C:\HarderWare\WxManager\WxManager.exe`).  Use the
Recipients tab to add email subscribers:

1. Enter an address in the lookup field and click **Search**.
2. Select a nearby METAR station from the results.
3. Fill in name, email, timezone, and preferred units.
4. Click **Save**.

The report service picks up new recipients automatically on its next cycle.

## 8. Start the Observability Stack (Optional)

The observability stack (OpenTelemetry Collector + Prometheus + Grafana)
is entirely optional.  If you skip it, the services run normally and no
metrics are exported — you simply won't have the Grafana dashboard.

To enable it:

1. **Turn telemetry on in configuration.**  In `appsettings.shared.json`,
   set `Telemetry:Enabled` to `true`:
   ```json
   "Telemetry": {
     "Enabled": true,
     "OtlpEndpoint": "http://localhost:4318/v1/metrics"
   }
   ```
   If telemetry is left `false`, WxParserSvc will not attempt to export
   metrics at all (no background HTTP traffic, nothing in the logs
   beyond a single "Telemetry disabled" line at startup).

2. **Start the Docker stack.**  From a command prompt (Docker Desktop
   must be running):
   ```
   cd C:\HarderWare\observability
   docker compose up -d
   ```

3. **Restart WxParserSvc** so it picks up the new configuration:
   ```powershell
   sc.exe stop WxParserSvc
   sc.exe start WxParserSvc
   ```

4. **Open the dashboards:**
   - **Grafana:** http://localhost:3000 (admin / grafana)
   - **Prometheus:** http://localhost:9090

The WxParser dashboard is provisioned automatically and displays in UTC.

To turn the stack off again: `docker compose down` from the same
directory, and set `Telemetry:Enabled` back to `false`.

## 9. Verify

| Check | How |
|---|---|
| Services running | `sc.exe query WxParserSvc` (and the other three) |
| Data being fetched | Look for new entries in `C:\HarderWare\Logs\wxparser-svc.log` |
| Reports sending | Check `C:\HarderWare\Logs\wxreport-svc.log` for "report(s) sent" |
| Maps rendering | Check `C:\HarderWare\plots\` for recent PNG files |
| Monitoring active | Check `C:\HarderWare\Logs\wxmonitor-svc.log` |

All log files use UTC timestamps.

## 10. Troubleshooting

| Symptom | Likely cause |
|---|---|
| "Connection string not found" | `appsettings.shared.json` missing or `ConnectionStrings:WeatherData` not set |
| No METAR data | `Fetch:HomeIcao` / `HomeLatitude` / `HomeLongitude` not configured |
| No reports sent | SMTP credentials or Claude API key missing from `appsettings.local.json` |
| Maps not rendering | `WxVis:CondaPythonExe` incorrect, or conda packages not installed |
| GFS fetch errors | WSL not running, or wgrib2 not installed |
| SQL timeout on GFS purge | Normal for large datasets; the system retries automatically |

For all issues, check the relevant log file in `{InstallRoot}\Logs\`.

## Uninstall

To remove the services:

```powershell
sc.exe stop WxParserSvc;  sc.exe delete WxParserSvc
sc.exe stop WxReportSvc;  sc.exe delete WxReportSvc
sc.exe stop WxMonitorSvc; sc.exe delete WxMonitorSvc
sc.exe stop WxVisSvc;     sc.exe delete WxVisSvc
```

Then delete the installation directory.  The `WeatherData` database can be
dropped via SQL Server Management Studio if no longer needed.
