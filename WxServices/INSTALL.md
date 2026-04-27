# HarderWare WxServices — Installation Guide

This guide walks through installing the HarderWare weather services system
on a fresh Windows machine.  The system fetches weather data (METAR, TAF,
GFS forecasts), generates natural-language weather reports via Claude AI,
renders weather maps, and emails personalised reports to subscribers.

> **Installed via the Setup.exe installer?**  The installer handles steps
> 3 (file placement) and 5 (service registration) automatically.  You
> still need to install the prerequisites (step 2), configure the system
> (step 4), start the services (step 6), and add recipients (step 7).

## Before You Begin — Windows Tools You'll Need

Several steps in this guide ask you to type commands.  Here's where to
type them:

- **PowerShell (as Administrator):** Click the **Start** button, type
  `PowerShell`, right-click **Windows PowerShell**, and choose
  **Run as administrator**.  Click **Yes** when prompted.

- **Command Prompt:** Click the **Start** button, type `cmd`, and press
  **Enter**.

- **Windows Services app:** Press **Win+R**, type `services.msc`, and
  press **Enter**.  This shows all Windows services with Start/Stop
  controls — a visual alternative to `sc.exe` commands.

When this guide says "open PowerShell as administrator," follow the first
set of instructions above.

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

Verify: open a **Command Prompt** (see Before You Begin) and type
`dotnet --info`, then press Enter.

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

### 2.3 wgrib2 (native Windows build)

GFS forecast-data processing uses `wgrib2`, NOAA's GRIB2 utility.  The
NOAA pre-built Windows binary is Cygwin-compiled and ships with its
required `cygwin1.dll` alongside, so it runs under any Windows identity
(including the `NT SERVICE\*` virtual accounts the services use).

1. Download the six files (total ≈10.7 MB) from NOAA's distribution:
   > https://ftp.cpc.ncep.noaa.gov/wd51we/wgrib2/Windows10/v3.1.3/

   Required: `wgrib2.exe`, `cygwin1.dll`, `cyggcc_s-seh-1.dll`,
   `cyggfortran-5.dll`, `cyggomp-1.dll`, `cygquadmath-0.dll`.

2. Place all six files in `{InstallRoot}\wgrib2\` (default
   `C:\HarderWare\wgrib2\`).  This path matches
   `WxPaths.Wgrib2DefaultPath` so no configuration override is needed
   unless you install wgrib2 somewhere else.

3. Verify from an ordinary Command Prompt:
   ```cmd
   C:\HarderWare\wgrib2\wgrib2.exe --version
   ```
   You should see a version line (e.g. `v3.1.3rc2 10/22/2023 ...`).
   wgrib2 exits with code 8 on `--version`; that's normal.

4. For services running as `NT SERVICE\*` accounts, grant each account
   Read + Execute on the folder:
   ```powershell
   icacls C:\HarderWare\wgrib2 /grant "NT SERVICE\WxParserSvc:(OI)(CI)RX" /T
   ```
   Only `WxParserSvc` actually invokes wgrib2; granting all four
   `NT SERVICE\Wx*Svc` accounts is a harmless convenience.

**Note.**  WSL is no longer required.  Prior releases invoked a
WSL-hosted `wgrib2` Linux binary via `wsl.exe`; that was retired in
1.3.6 (WX-33) when the services moved to virtual service accounts,
which have no WSL distro.

### 2.4 Miniconda and the wxvis Environment

Miniconda provides the Python environment for weather map rendering.

1. Download and install Miniconda:
   > https://docs.conda.io/en/latest/miniconda.html

2. Open the **Anaconda Prompt** (click Start, type `Anaconda Prompt`,
   press Enter) and create the wxvis environment:
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

2. Verify: open a **Command Prompt** and type `docker info`.

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

### What3Words API Key (Optional)

If you want to enter recipient addresses as What3Words (e.g.
`///offer.loops.carb`), get a free API key from
https://developer.what3words.com/public-api and add it to
`{InstallRoot}\appsettings.local.json` (create the file if it does not
already exist):

```json
{
  "What3Words": {
    "ApiKey": "your-w3w-api-key"
  }
}
```

If the key is missing, `///` addresses fail with a logged error; street
addresses and `lat, lon` decimal entries continue to work normally.

### Optional settings

| Setting | Default | Description |
|---|---|---|
| `ConnectionStrings:WeatherData` | `Server=.\SQLEXPRESS;...` | Change if your SQL Server instance differs |
| `Fetch:BoundingBoxDegrees` | `9` | Radius around home location for data collection |
| `Gfs:Wgrib2Path` | `{InstallRoot}\wgrib2\wgrib2.exe` | Absolute Windows path to `wgrib2.exe`.  Leave empty to use the default derived from `InstallRoot`. |
| `WxVis:MapExtent` | `south_central` | Map extent: preset name or W,E,S,N coordinates |
| `Monitor:AlertEmail` | (empty) | Email address for service health alerts |

## 5. Register Windows Services

> **Installed via Setup.exe?** Skip this step — the installer already
> registered the services.

If you installed manually (without the installer), open **PowerShell as
administrator** (see Before You Begin) and type these commands.  Adjust
the path if your install directory is not `C:\HarderWare`:

```powershell
$ir = "C:\HarderWare"

sc.exe create WxParserSvc  binPath= "$ir\services\WxParser.Svc\WxParser.Svc.exe"
sc.exe create WxReportSvc  binPath= "$ir\services\WxReport.Svc\WxReport.Svc.exe"
sc.exe create WxMonitorSvc binPath= "$ir\services\WxMonitor.Svc\WxMonitor.Svc.exe"
sc.exe create WxVisSvc     binPath= "$ir\services\WxVis.Svc\WxVis.Svc.exe"
```

## 6. Start Services

**Startup order matters:** WxParserSvc must run first so weather data is
available when WxReportSvc starts.

**Using the Windows Services app (recommended for most users):**

1. Open the **Windows Services app** (see Before You Begin).
2. Scroll down to find **WxParserSvc**.  Right-click it and choose **Start**.
3. Wait ~10 minutes for the first data fetch cycle to complete.
4. Start the remaining three services the same way: **WxReportSvc**,
   **WxMonitorSvc**, **WxVisSvc**.

**Using PowerShell (alternative):**

Open **PowerShell as administrator** and type:

```powershell
sc.exe start WxParserSvc
```

Wait ~10 minutes, then:

```powershell
sc.exe start WxReportSvc
sc.exe start WxMonitorSvc
sc.exe start WxVisSvc
```

## 7. Add Recipients

Open **WxManager** (`C:\HarderWare\WxManager\WxManager.exe`).  Use the
Recipients tab to add email subscribers:

1. Enter the recipient's location in the address field and click **Look Up**. Three forms are accepted:
   - **Street address** (e.g. `123 Main St, Springfield, IL`) — resolved via Nominatim (OpenStreetMap).
   - **What3Words** (e.g. `///offer.loops.carb`) — resolved via the What3Words API; requires the optional API key in `appsettings.local.json` (see §4).
   - **Decimal coordinates** (e.g. `30.07, -95.55`) — parsed locally with no API call. You will then fill in the Locality field manually.
2. Select a nearby METAR station from the results.
3. Fill in name, email, timezone, and preferred units.
4. Click **Save**.

If none of the three input forms resolves your address, you can fill in
Latitude, Longitude, Locality, METAR ICAO, and TAF ICAO directly — all of
those fields are editable.

The report service picks up new recipients automatically on its next cycle.

> **Tip:** WxManager's **Setup tab** runs prerequisite checks with
> pass/fail indicators for SQL Server, wgrib2, conda, and Docker.
> Use it to verify that everything is working before adding recipients.

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

2. **Start the Docker stack.**  Open a **Command Prompt** (Docker Desktop
   must be running) and type these two commands:
   ```
   cd C:\HarderWare\observability
   docker compose up -d
   ```

3. **Restart WxParserSvc** so it picks up the new configuration.
   Open the **Windows Services app**, find **WxParserSvc**, right-click
   it, choose **Restart**.  (Or in PowerShell as administrator:
   `sc.exe stop WxParserSvc` then `sc.exe start WxParserSvc`.)

4. **Open the dashboards:**
   - **Grafana:** http://localhost:3000 (admin / grafana)
   - **Prometheus:** http://localhost:9090

The WxParser dashboard is provisioned automatically and displays in UTC.

To turn the stack off again: `docker compose down` from the same
directory, and set `Telemetry:Enabled` back to `false`.

## 9. Verify

| Check | How |
|---|---|
| Services running | Open the **Windows Services app** and confirm WxParserSvc, WxReportSvc, WxMonitorSvc, and WxVisSvc all show **Running** |
| Data being fetched | Open `C:\HarderWare\Logs\wxparser-svc.log` in Notepad — look for recent entries |
| Reports sending | Open `C:\HarderWare\Logs\wxreport-svc.log` in Notepad — look for "report(s) sent" |
| Maps rendering | Open `C:\HarderWare\plots\` in File Explorer — look for recent PNG files |
| Monitoring active | Open `C:\HarderWare\Logs\wxmonitor-svc.log` in Notepad |

All log files use UTC timestamps.

## 10. Troubleshooting

| Symptom | Likely cause |
|---|---|
| "Connection string not found" | `appsettings.shared.json` missing or `ConnectionStrings:WeatherData` not set |
| No METAR data | `Fetch:HomeIcao` / `HomeLatitude` / `HomeLongitude` not configured |
| No reports sent | SMTP credentials or Claude API key not set — use WxManager → Configure |
| Maps not rendering | `WxVis:CondaPythonExe` incorrect, or conda packages not installed |
| GFS fetch errors | `wgrib2.exe` missing at the configured path, or the service account lacks RX on the wgrib2 folder |
| SQL timeout on GFS purge | Normal for large datasets; the system retries automatically |

For all issues, check the relevant log file in `{InstallRoot}\Logs\`.

## Uninstall

**If you used the Setup.exe installer:**

1. Open **Windows Settings → Apps → Installed apps** (or **Add or Remove
   Programs** on older Windows 10 builds).
2. Find **HarderWare WxServices** in the list and click **Uninstall**.

The uninstaller stops and removes all four services automatically.

**If you installed manually:**

Open **PowerShell as administrator** and type:

```powershell
sc.exe stop WxParserSvc;  sc.exe delete WxParserSvc
sc.exe stop WxReportSvc;  sc.exe delete WxReportSvc
sc.exe stop WxMonitorSvc; sc.exe delete WxMonitorSvc
sc.exe stop WxVisSvc;     sc.exe delete WxVisSvc
```

Then delete the installation directory.  The `WeatherData` database can be
dropped via SQL Server Management Studio if no longer needed.
