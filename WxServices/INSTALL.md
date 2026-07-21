# HarderWare WxServices — Installation Guide

This guide walks through installing the HarderWare weather services system
on a fresh Windows machine.  The system fetches weather data (METAR, TAF,
GFS forecasts), generates natural-language weather reports via Claude AI,
renders weather maps, and emails personalised reports to subscribers.

> **Read this first — how the system runs today.**
>
> The four weather services (WxParser, WxReport, WxMonitor, WxVis) run as
> **Docker containers**.  They are no longer native Windows services, and
> the native services have been removed — any older instructions telling
> you to register `WxParserSvc` and friends with `sc.exe` are obsolete.
>
> The container images are **built from the source repository**.  There is
> no published image to download, so the installer alone cannot give you a
> running system: it places the desktop applications (WxManager, WxViewer),
> the documentation, and support files, but starting the services requires a
> source checkout and Docker.
>
> **If you have only the installer and no source repository, the services
> cannot be started yet.**  A self-contained, source-free installation is
> planned but does not exist today.  This guide is honest about that rather
> than walking you into a dead end; the developer guide
> (`DEVELOPER-README.md`) covers the source-based path in full.

## Before You Begin — Windows Tools You'll Need

Several steps in this guide ask you to type commands.  Here's where to
type them:

- **PowerShell (as Administrator):** Click the **Start** button, type
  `PowerShell`, right-click **Windows PowerShell**, and choose
  **Run as administrator**.  Click **Yes** when prompted.

- **Command Prompt:** Click the **Start** button, type `cmd`, and press
  **Enter**.

When this guide says "open PowerShell as administrator," follow the first
set of instructions above.

## 1. System Requirements

- Windows 10 or 11 (64-bit)
- 8 GB RAM minimum (16 GB recommended — SQL Server and the containers
  share memory)
- 10 GB free disk space
- Internet connection (for weather data feeds and the Claude API)
- Docker Desktop (see below — the services run as containers)

## 2. Install Prerequisites

### 2.1 .NET 8 Runtime

Download and install the **.NET 8 Runtime** (not the SDK, unless you plan
to develop):

> https://dotnet.microsoft.com/download/dotnet/8.0

Verify: open a **Command Prompt** (see Before You Begin) and type
`dotnet --info`, then press Enter.

This is needed by the desktop applications (WxManager and WxViewer), which
run natively on Windows.  The four services carry their own .NET runtime
inside their container images.

### 2.2 SQL Server Express

Download and install **SQL Server Express** (free):

> https://www.microsoft.com/sql-server/sql-server-downloads

During setup:
- Use the default instance name `SQLEXPRESS`.
- Enable **Mixed Mode** authentication.  The containers authenticate over
  TCP with a SQL login, which Windows-only authentication cannot serve.

After installation:
1. Open **SQL Server Configuration Manager**.
2. Under *SQL Server Network Configuration → Protocols for SQLEXPRESS*,
   ensure **TCP/IP** is enabled.
3. Restart the SQL Server service.

TCP/IP is not optional here: the containers reach the database through the
host's TCP port, so a SQL Server listening only on shared memory or named
pipes is unreachable from them.

The database itself (`WeatherData`) is created automatically — no manual
SQL scripts are needed.

### 2.3 Docker Desktop (Required)

Docker runs the four weather services.  This is the entire runtime, not an
optional extra.

1. Download and install Docker Desktop:
   > https://www.docker.com/products/docker-desktop/

2. Verify: open a **Command Prompt** and type `docker info`.

3. Leave Docker Desktop set to start when you log in.  The service
   containers use a restart policy that brings them back automatically
   after a crash or a reboot, but that only works once the Docker engine
   itself is running.

Docker also powers the optional Prometheus and Grafana dashboards
(section 8).

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
├── WxManager\          ← management GUI (runs natively)
├── WxViewer\           ← desktop viewer (runs natively)
├── WxVis\              ← Python rendering scripts
├── tools\              ← bundled support tools
├── observability\      ← Prometheus/Grafana compose files
└── services\           ← see note below
    ├── WxParser.Svc\
    ├── WxReport.Svc\
    ├── WxMonitor.Svc\
    └── WxVis.Svc\
```

> **About `services\`:** the installer still lays down the four service
> executables here, left over from when they ran as native Windows services.
> They are **not** what runs today — the services run as containers built from
> the source repository — so nothing launches these and you can ignore them.
> They are listed only so the directory does not look like an anomaly.

If you use a directory other than `C:\HarderWare`, update the `InstallRoot`
setting in `appsettings.shared.json` (see below).

`Logs\` and `plots\` are shared: the containers write into them through a
bind mount, and the native desktop applications read the same files from
the host, so both halves of the system agree on one location.

## 4. Configure

The easiest way to configure the system is to use **WxManager → Configure
tab**, which provides a graphical editor with test buttons for database
connectivity, SMTP, and the Claude API.

**Where settings actually live.**  The database is the runtime source of
truth for configuration.  Values you edit on the Configure tab are written
to the database, not to a file, and the services read them from there.  A
small set of *bootstrap* settings must stay in files, because they are
needed before the database can be opened:

| Setting | Where it lives | Why |
|---|---|---|
| `ConnectionStrings:WeatherData` | file | It is the key to the store that holds everything else |
| `Database:StartupRetry:*` | file | Governs how the service retries opening that store |
| `Telemetry:*` | file | Read during start-up, before configuration is loaded |
| `Claude:TimeoutSeconds` | file | Applied when the HTTP client is constructed at start-up |
| everything else | database | Editable at runtime; last-wins over the file layers |

### Required settings

| Setting | Description | Example |
|---|---|---|
| `InstallRoot` | Installation directory. Set in `appsettings.shared.json` **before deploy** (or the `WXSERVICES_INSTALL_ROOT` environment variable); the Configure tab shows it **read-only**. | `C:\\HarderWare` |

The home location settings — the ICAO code of your nearest METAR station,
your latitude and longitude, the fetch bounding box, and the map extent —
are **not** editable from the Configure tab.  They are set once, during
first-time setup, by the setup console described in `DEVELOPER-README.md`,
and stored in the database.  Changing them later means re-running that
console or editing the `Config` table directly.  This is deliberate: they
are foundational values that the rest of the system derives from, not
day-to-day settings.

To find your nearest METAR station, search for your location at:
> https://aviationweather.gov/data/metar/

### Secrets (SMTP credentials, Claude API key, What3Words key)

Secrets are stored in the database, never in configuration files.  Use
**WxManager → Configure tab** to enter:

- **SMTP Username** — your Gmail address
- **SMTP Password** — a Gmail App Password (not your regular password)
- **SMTP From Address** — typically the same Gmail address
- **Claude API Key** — begins with `sk-ant-...`
- **What3Words API Key** — optional; see below

Click **Save** in the Configure tab and the credentials are written
directly to the database.  They never appear in any file on disk.

**Gmail App Password:** Go to https://myaccount.google.com/apppasswords
to generate an app-specific password (requires 2-factor authentication).

**Claude API Key:** Sign up at https://console.anthropic.com/ and create
an API key.

**What3Words API Key (optional):** If you want to enter recipient addresses
as What3Words (e.g. `///offer.loops.carb`), get a free API key from
https://developer.what3words.com/public-api and enter it on the Configure
tab like any other secret.  If the key is missing, `///` addresses fail
with a logged error; street addresses and `lat, lon` decimal entries
continue to work normally.

### Optional settings

| Setting | Default | Description |
|---|---|---|
| `ConnectionStrings:WeatherData` | `Server=.\SQLEXPRESS;...` | Change if your SQL Server instance differs |
| `Monitor:AlertEmail` | (empty) | Email address for service health alerts |

## 5. Start the Services

The four services are started together with Docker Compose, from the
`services` directory of the source repository:

```cmd
cd <repo>\services
docker compose up -d
```

The first run builds the four images, which takes several minutes.  Later
runs reuse them and start in seconds.

**Startup order is handled for you.**  The services coordinate through the
database rather than by talking to each other, so they can start in any
order; WxReport simply produces nothing until WxParser has completed a
fetch cycle (allow roughly 10 minutes for the first one).

### Everyday container operations

Run these from the same `services` directory:

| Task | Command |
|---|---|
| Start (or restart after a config change) | `docker compose up -d` |
| Pause the stack, keeping the containers | `docker compose stop` |
| Tear the stack down, removing the containers | `docker compose down` |
| Check what is running | `docker compose ps` |
| Watch one service's output | `docker compose logs -f wxreport` |
| Restart a single service | `docker compose restart wxreport` |
| Rebuild after a code change | `docker compose up -d --build` |

**`stop` and `down` are not the same thing, and the difference shows up after a
reboot.** The containers carry a restart policy that brings them back
automatically after a crash or a reboot — but only containers that still exist
and were not stopped by hand. `stop` leaves a container in place and
deliberately *stopped*, so it stays down until you start it again, even across
a reboot. `down` removes the containers altogether, so nothing returns until the
next `docker compose up -d`. Neither is wrong; just pick the one you meant.

All of this assumes Docker Desktop itself starts at login — the engine has to be
running before it can restart anything.

The complete operational reference — restart policy, reboot recovery, and the
autoheal sidecar — is the runbook at
`docs/policies-and-procedures/container-stack-operations.md` in the source
repository.

## 6. Add Recipients

Open **WxManager** (`C:\HarderWare\WxManager\WxManager.exe`).  Use the
Recipients tab to add email subscribers:

1. Enter the recipient's location in the address field and click **Look Up**. Three forms are accepted:
   - **Street address** (e.g. `123 Main St, Springfield, IL`) — resolved via Nominatim (OpenStreetMap).
   - **What3Words** (e.g. `///offer.loops.carb`) — resolved via the What3Words API; requires the optional API key entered on the Configure tab (see section 4).
   - **Decimal coordinates** (e.g. `30.07, -95.55`) — parsed locally with no API call. You will then fill in the Locality field manually.
2. Select a nearby METAR station from the results.
3. Fill in name, email, timezone, and preferred units.
4. Click **Save**.

If none of the three input forms resolves your address, you can still
fill in Locality, METAR ICAO, and TAF ICAO directly. Latitude and
Longitude remain read-only; for direct coordinates, enter `lat, lon`
in the Address field and click **Look Up** so nearby-station discovery
still runs.

The report service picks up new recipients automatically on its next cycle.

> **Tip:** WxManager's **Setup tab** runs prerequisite checks with
> pass/fail indicators for SQL Server, the database, and Docker.
> Use it to verify that everything is working before adding recipients.

## 7. Start the Observability Stack (Optional)

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

   If telemetry is left `false`, the services will not attempt to export
   metrics at all (no background HTTP traffic, nothing in the logs
   beyond a single "Telemetry disabled" line at startup).

2. **Create the Grafana admin-password file.**  Open Notepad and create
   `<repo>\observability\.env` — the same directory you run `docker compose`
   from in step 3, **not** the `observability\` folder under your install root —
   containing a single line:

   ```properties
   GRAFANA_ADMIN_PASSWORD=<your-chosen-strong-password>
   ```

   Pick a strong password and save it somewhere safe (a password manager
   is the cleanest option) — you'll need it to log into Grafana.
   Without this file, `docker compose up` will refuse to start the stack
   and print a clear error mentioning `GRAFANA_ADMIN_PASSWORD`.

3. **Start the Docker stack.**  Open a **Command Prompt** (Docker Desktop
   must be running) and type these two commands:

   ```cmd
   cd <repo>\observability
   docker compose up -d
   ```

4. **Restart the services** so they pick up the new configuration:

   ```cmd
   cd <repo>\services
   docker compose restart
   ```

5. **Open the dashboards:**
   - **Grafana:** http://localhost:3000 (log in as `admin` with the
     password you set in step 2)
   - **Prometheus:** http://localhost:9090

The WxParser dashboard is provisioned automatically and displays in UTC.

To turn the stack off again: `docker compose down` from the same
directory, and set `Telemetry:Enabled` back to `false`.

## 8. Verify

| Check | How |
|---|---|
| Services running | `docker compose ps` from the `services` directory — all four should show as running and healthy |
| Data being fetched | Open `C:\HarderWare\Logs\wxparser-svc.log` in Notepad — look for recent entries |
| Reports sending | Open `C:\HarderWare\Logs\wxreport-svc.log` in Notepad — look for "report(s) sent" |
| Maps rendering | Open `C:\HarderWare\plots\` in File Explorer — look for recent PNG files |
| Monitoring active | Open `C:\HarderWare\Logs\wxmonitor-svc.log` in Notepad |

All log files use UTC timestamps.  The containers write them to the host
directory through a bind mount, so you read them exactly as before.

## 9. Troubleshooting

| Symptom | Likely cause |
|---|---|
| Containers won't start | Docker Desktop is not running, or the images have not been built yet — try `docker compose up -d --build` |
| "Connection string not found" | `appsettings.shared.json` missing or `ConnectionStrings:WeatherData` not set |
| Containers start but cannot reach the database | SQL Server TCP/IP disabled, Mixed Mode authentication off, or the SQL login's password is wrong |
| No METAR data | Home location was never set by first-time setup — check the `Config` table |
| No reports sent | SMTP credentials or Claude API key not set — use WxManager → Configure |
| Maps not rendering | Check `wxvis-svc.log`; the rendering stack lives inside the WxVis image, so this is not a host Python problem |
| Logs are empty or stale | The log bind mount is broken — recreate the containers with `docker compose up -d --force-recreate` |
| SQL timeout on GFS purge | Normal for large datasets; the system retries automatically |

For all issues, check the relevant log file in `{InstallRoot}\Logs\`, and
the container's own output with `docker compose logs <service>`.

## Uninstall

**Stop and remove the containers** from the `services` directory:

```cmd
docker compose down
```

Add `--rmi all` to that command if you also want to delete the built
images.  Do the same from the `observability` directory if you started the
dashboards.

**If you used the Setup.exe installer:**

1. Open **Windows Settings → Apps → Installed apps** (or **Add or Remove
   Programs** on older Windows 10 builds).
2. Find **HarderWare WxServices** in the list and click **Uninstall**.

**If you installed manually:** delete the installation directory.

The `WeatherData` database can be dropped via SQL Server Management Studio
if no longer needed.  Note that it holds every secret you entered on the
Configure tab, so dropping it discards those too.
