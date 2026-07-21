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
> no published image to download, and **there is no packaged installer** —
> the `Setup.exe` this guide once described was written for the native
> services and was retired along with them.
>
> **Installing therefore means: a source checkout, plus Docker.**  A
> self-contained, source-free installation is planned but does not exist
> today.  This guide is honest about that rather than walking you into a
> dead end; the developer guide (`DEVELOPER-README.md`) covers the
> source-based path in full.

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
- Git, and access to the source repository — installing builds from source
  (§3), so the repository is a prerequisite, not an optional extra

## 2. Install Prerequisites

### 2.1 .NET 8 SDK

Download and install the **.NET 8 SDK** — the SDK, not just the Runtime:

> https://dotnet.microsoft.com/download/dotnet/8.0

The Runtime alone is not enough.  Because there is no packaged installer,
every install builds from source (§3): the setup console runs via
`dotnet run`, and `Deploy-WxService.ps1` runs `dotnet publish`.  Both are
SDK commands, and neither exists in a Runtime-only installation.

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
(section 7).

## 3. Install the Product

**There is no distribution archive and no installer.**  Installing means
getting the source repository and letting its scripts build the product in
place.  The full sequence — clone, git hooks, setup console, deploy — is in
`DEVELOPER-README.md`; the short version is:

1. Get the source repository onto the machine.
2. Run the setup console once.  It creates the database and writes the
   foundational settings (home location, connection string, install root).
3. From the `WxServices` directory, in an **elevated** PowerShell:

   ```powershell
   .\Deploy-WxService.ps1 all
   ```

   This publishes WxManager and WxViewer natively into the install root and
   builds and starts the four service containers.

The install root — `C:\HarderWare` by default — is **created by that deploy**,
not unpacked from an archive.  On a fresh machine it holds only what the deploy
actually produces:

```text
C:\HarderWare\
├── appsettings.local.json  ← written by the setup console (§4)
├── Logs\                   ← created by the deploy; bind-mounted into the containers
├── plots\                  ← created by Docker as a bind-mount source
├── translation-qa\         ← created by Docker as a bind-mount source
├── WxManager\              ← management GUI, published here (runs natively)
└── WxViewer\               ← desktop viewer, published here (runs natively)
```

A `temp\` directory appears later — WxManager creates it the first time you
**save** on the Configure tab, not at deploy time and not merely by launching
the application.

An **older machine may also carry `tools\`, `WxVis\`, `services\`, `wgrib2\` or
`observability\`.**  Those were placed by the retired installer; nothing creates
or reads them now — the Python rendering scripts and `wgrib2` live inside the
container images, and the observability compose files stay in the source
repository.  They are inert leftovers, safe to delete.

`appsettings.shared.json` and `log4net.shared.config` are **not** at the top
level: each application gets its own build-generated copy beside its binaries
(`WxManager\appsettings.shared.json`, and so on), and the containers carry
theirs inside the image.  Edit the canonical copy in the source repository and
redeploy — never the generated copies.

The Prometheus/Grafana compose files stay in the source repository under
`observability\` (§7); nothing copies them into the install root.

To install somewhere other than `C:\HarderWare`, set `InstallRoot` in the
repository's `appsettings.shared.json` before deploying — the deploy script
reads it to decide where to publish and where to point the container bind
mounts.

`Logs\`, `plots\` and `translation-qa\` are shared: the containers write into
them through bind mounts and the native desktop applications read the same
files from the host, so both halves of the system agree on one location.

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
| `Monitor:AlertEmail` | (empty) | Email address for service health alerts |

**There are two different connection strings, and mixing them up is the most
common setup failure.**  They are not interchangeable:

| Which | Looks like | Used by |
|---|---|---|
| **Native / management** | `Server=.\SQLEXPRESS;…;Trusted_Connection=True` | WxManager and the setup console, running on the Windows host under your own Windows account |
| **Container / service** | `Server=host.docker.internal,1433;…;User Id=wxservices;Password=…` | The four service containers |

A Linux container has no Windows identity, so it cannot use
`Trusted_Connection`, and `.\SQLEXPRESS` is a host-local instance name that
means nothing inside a container. That is why SQL Server needs both Mixed Mode
and TCP/IP (section 2.2). The setup console writes the container form into each
service's own configuration file for you — you should not need to hand-edit
either one.

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
| Start the stack | `docker compose up -d` |
| Apply an edited `appsettings.local.json` | `docker compose restart` (or `up -d --force-recreate`) |
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
| Data being fetched | Open `{InstallRoot}\Logs\wxparser-svc.log` in Notepad — look for recent entries |
| Reports sending | Open `{InstallRoot}\Logs\wxreport-svc.log` in Notepad — look for "report(s) sent" |
| Maps rendering | Open `{InstallRoot}\plots\` in File Explorer — look for recent PNG files |
| Monitoring active | Open `{InstallRoot}\Logs\wxmonitor-svc.log` in Notepad |

`{InstallRoot}` is `C:\HarderWare` unless you changed it (section 3).  All log
files use UTC timestamps.  The containers write them to the host directory
through a bind mount, so you read them exactly as before.

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

**Next — only if this installation was made by the retired `Setup.exe`:**
open **Windows Settings → Apps → Installed apps**, find **HarderWare
WxServices**, and uninstall it there.  Do this **before** deleting anything
by hand: the uninstaller lives inside the installation directory, so
deleting the directory first destroys the only thing that can clear the
Installed apps entry.  A current installation has no such entry and you can
skip this step.

**Finally, delete the installation directory.**  Nothing else is registered
with Windows — there are no services to remove.

The `WeatherData` database can be dropped via SQL Server Management Studio
if no longer needed.  Note that it holds every secret you entered on the
Configure tab, so dropping it discards those too.
