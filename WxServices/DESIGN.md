# WxServices System — Design Document

**Living document.** Update this file whenever the code changes in a meaningful way.

---

> **Viewing this document with diagrams**
>
> The architecture diagrams in this file are written in [Mermaid](https://mermaid.js.org/) and must be rendered to be readable. Raw Markdown shows them as fenced code blocks.
>
> **One-time setup in VS Code:**
> 1. Open the Extensions panel — **Ctrl+Shift+X**
> 2. Search for **Markdown Preview Mermaid Support**
> 3. Install the extension by **Matt Bierner**
>
> **Viewing the rendered document:**
> - **Ctrl+Shift+V** — opens a rendered preview in a new tab
> - **Ctrl+K V** — opens the rendered preview side-by-side with the source
>
> The preview updates automatically as you edit the source.

---

## Table of Contents

1. [Purpose](#1-purpose)
2. [System Architecture](#2-system-architecture)
3. [Solution Structure](#3-solution-structure)
4. [Service Details](#4-service-details)
   - [WxParser.Svc — Data Fetcher](#41-wxparsersvc--data-fetcher)
   - [WxReport.Svc — Report Generator](#42-wxreportsvc--report-generator)
   - [WxVis.Svc — Map Renderer](#43-wxvissvc--map-renderer)
   - [WxVis — Python Visualisation](#44-wxvis--python-visualisation)
   - [WxMonitor.Svc — Health Monitor](#45-wxmonitorsvc--health-monitor)
   - [WxViewer — Desktop Map Viewer](#46-wxviewer--desktop-map-viewer)
   - [WxManager — Management GUI](#47-wxmanager--management-gui)
5. [Class Libraries](#5-class-libraries)
6. [Data Model](#6-data-model)
7. [Configuration Guide](#7-configuration-guide)
8. [External Dependencies](#8-external-dependencies)
9. [Installation and Deployment](#9-installation-and-deployment)
10. [Known Limitations and Future Work](#10-known-limitations-and-future-work)
11. [Observability](#11-observability)

---

## 1. Purpose

WxServices is a set of Windows services that:

- Periodically fetch METAR and TAF aviation weather reports from the Aviation Weather Center API and store them in a local SQL Server database.
- Download GFS numerical weather prediction model data from NOAA (via the AWS Open Data mirror) and extract gridded medium-range forecasts covering temperature, wind, cloud cover, precipitation rate, and convective energy (CAPE) for the configured region.
- Generate friendly, plain-English (or other language) weather summaries using Anthropic's Claude AI and email them to a configured list of recipients.
- Render weather visualisation maps (synoptic analysis, GFS forecast parameter maps, and per-recipient meteograms) automatically via WxVis.Svc, which invokes the WxVis Python project after each data cycle.
- Embed a 48-hour meteogram in each recipient's weather report email and attach full-period meteograms to the WxViewer Meteograms tab.
- Provide a local WPF desktop viewer (WxViewer) for browsing and animating the generated maps and meteograms side-by-side.
- Monitor the health of the above services and send alert emails if errors occur or a service goes silent.
- Provide a local WPF management GUI (WxManager) for adding and editing recipients and sending operator service announcements to all subscribers.

Recipients each have their own location. The system automatically resolves the nearest METAR and TAF reporting stations for each recipient on first run and caches the result. Daily reports are sent at each recipient's configured local time; additional reports are triggered by significant weather changes.

---

## 2. System Architecture

### 2.1 Overview

Three Windows services share a log directory and a SQL Server database. WxParser.Svc feeds the database; WxReport.Svc reads from it; WxMonitor.Svc watches both.

```mermaid
flowchart TD
    AWC["Aviation Weather Center API"]
    DB[("SQL Server WeatherData DB")]
    CLAUDE["Anthropic Claude API"]
    RECIPIENTS["Email recipients"]
    ALERTS["Alert email"]
    LOGS["C:\\HarderWare\\Logs\\"]

    NOMADS["NOAA / AWS Open Data (GFS GRIB2)"]
    PARSER["WxParser.Svc (every 10 min / 60 min GFS)"]
    REPORT["WxReport.Svc (every 5 min)"]
    MONITOR["WxMonitor.Svc (every 5 min)"]

    WVISSVC["WxVis.Svc (Windows service — map renderer)"]
    WXVIS["WxVis Python scripts"]
    PLOTS["C:\\HarderWare\\plots\\"]
    VIEWER["WxViewer (WPF desktop app)"]

    AWC -->|METAR + TAF| PARSER
    NOMADS -->|GFS GRIB2 files| PARSER
    PARSER -->|store observations + GFS grid| DB
    PARSER -->|log + heartbeat| LOGS

    DB -->|latest conditions| REPORT
    CLAUDE -->|report text| REPORT
    REPORT -->|email| RECIPIENTS
    REPORT -->|log + heartbeat| LOGS

    DB -->|METAR + GFS grid| WVISSVC
    WVISSVC -->|invoke| WXVIS
    WXVIS -->|timestamped PNG maps| PLOTS
    WVISSVC -->|log + heartbeat| LOGS

    PLOTS -->|file scan| VIEWER

    LOGS -->|scan| MONITOR
    MONITOR -->|alert email| ALERTS
```

---

### 2.2 WxParser.Svc — METAR/TAF data flow

```mermaid
flowchart TD
    AWC["AWC METAR/TAF API (avwx.gov)"]
    AWC2["AWC Airport API (ICAO to coordinates)"]
    DB[("SQL Server WeatherData DB")]
    PLOG["wxparser-svc.log"]
    PHB["wxparser-heartbeat.txt"]

    START([Cycle starts]) --> COORDS{Coordinates configured?}
    COORDS -->|Yes| FETCH
    COORDS -->|No| LOOKUP["Look up HomeIcao via AWC Airport API"]
    LOOKUP --> SAVE["Cache to config"]
    SAVE --> FETCH

    FETCH["Fetch METARs + TAFs for bounding box"] --> AWC
    AWC -->|XML reports| PARSE["Parse + deduplicate"]
    PARSE --> DB
    DB --> DONE([Cycle complete])
    DONE --> PHB
    DONE --> PLOG

    LOOKUP --> AWC2
    AWC2 --> LOOKUP
```

---

### 2.3 WxParser.Svc — GFS data flow

Runs on a separate timer (default: every 60 minutes).

```mermaid
flowchart TD
    NOMADS["NOAA / AWS S3 Open Data (GFS pgrb2 0.25°)"]
    WGRIB2["wgrib2.exe (native Windows)"]
    DB[("SQL Server WeatherData DB")]
    TEMP["Temp GRIB2 file (C:\\HarderWare\\temp)"]

    START([GFS cycle starts]) --> PENDING{Incomplete run in DB?}
    PENDING -->|Yes| RESUME["Resume that run"]
    PENDING -->|No| LATEST["Compute latest available model run"]
    LATEST --> KNOWN{Already complete?}
    KNOWN -->|Yes| SKIP([Skip])
    KNOWN -->|No| RESUME

    RESUME --> LOOP["For each forecast hour 0–120"]
    LOOP --> STORED{Hour already stored?}
    STORED -->|Yes| LOOP
    STORED -->|No| IDX["Fetch .idx inventory file"]
    IDX --> NOTFOUND{404?}
    NOTFOUND -->|Yes| STOP([Stop — run not yet complete])
    NOTFOUND -->|No| RANGES["Parse byte ranges for 7 target variables"]
    RANGES --> DOWNLOAD["Download byte ranges → temp GRIB2"]
    DOWNLOAD --> TEMP
    TEMP --> WGRIB2
    WGRIB2 -->|"-small_grib (bbox crop)"| SUBGRID["Sub-grid GRIB2"]
    SUBGRID --> WGRIB2
    WGRIB2 -->|"-csv"| CSV["CSV of grid values"]
    CSV --> ASSEMBLE["Assemble GfsGridPoint entities"]
    ASSEMBLE --> DB
    DB --> CLEANUP["Delete temp files"]
    CLEANUP --> LOOP

    LOOP -->|All hours stored| MARK["Mark run IsComplete = true in GfsModelRuns"]
    MARK --> PURGE["Purge old runs (retain 2)"]
```

---

### 2.4 WxReport.Svc — data flow

```mermaid
flowchart TD
    DB[("SQL Server WeatherData DB")]
    NOM["Nominatim Geocoding API"]
    AWC2["AWC Airport API (ICAO to coordinates)"]
    CLAUDE["Anthropic Claude API"]
    SMTP["Gmail SMTP"]
    RHB["wxreport-heartbeat.txt"]
    RLOG["wxreport-svc.log"]

    START([Cycle starts]) --> EACH["For each recipient"]

    EACH --> RESOLVED{Location resolved?}
    RESOLVED -->|No| GEO["Geocode address via Nominatim"]
    GEO --> NEAREST["Find nearest METAR + TAF station in DB"]
    NEAREST --> CACHE["Cache to Recipients table in DB"]
    CACHE --> SNAP

    RESOLVED -->|Yes| SNAP["Build WeatherSnapshot from DB\n(METAR + TAF + GFS forecast)"]
    DB --> SNAP

    SNAP --> FP["Compute change fingerprint"]
    FP --> SEND{Should send?}
    SEND -->|No| EACH
    SEND -->|Yes| DESC["SnapshotDescriber to structured text"]
    DESC --> CLAUDE
    CLAUDE -->|report text| EMAIL["Send via SMTP"]
    EMAIL --> SMTP
    SMTP --> STATE["Update RecipientState in DB"]
    STATE --> EACH

    EACH --> DONE([Cycle complete])
    DONE --> RHB
    DONE --> RLOG

    GEO --> NOM
    NOM --> GEO
    NEAREST --> AWC2
    AWC2 --> NEAREST
```

---

### 2.5 WxMonitor.Svc — data flow

```mermaid
flowchart TD
    PLOG["wxparser-svc.log"]
    RLOG["wxreport-svc.log"]
    PHB["wxparser-heartbeat.txt"]
    RHB["wxreport-heartbeat.txt"]
    STATE["wxmonitor-state.json (last-seen timestamps)"]
    SMTP["Gmail SMTP"]
    MLOG["wxmonitor-svc.log"]

    START([Cycle starts]) --> LOAD["Load state"]
    LOAD --> STATE

    LOAD --> EACH["For each watched service"]

    EACH --> SCAN["Scan log file for ERROR+ entries newer than last seen"]
    PLOG --> SCAN
    RLOG --> SCAN

    SCAN --> NEWLOG{New errors found?}
    NEWLOG -->|Yes, not on cooldown| LOGEMAIL["Send log-error alert email"]
    NEWLOG -->|No / on cooldown| HB

    LOGEMAIL --> SMTP

    HB["Read heartbeat file and check age"]
    PHB --> HB
    RHB --> HB

    HB --> STALE{Heartbeat stale?}
    STALE -->|Yes, not on cooldown| HBEMAIL["Send heartbeat-stale alert email"]
    STALE -->|No / on cooldown| EACH
    HBEMAIL --> SMTP

    SMTP --> EACH

    EACH --> SAVE["Save updated state"]
    SAVE --> STATE
    SAVE --> DONE([Cycle complete])
    DONE --> MLOG
```

---

## 3. Solution Structure

```
WxServices/
├── DESIGN.md                        ← this file
├── WxServices.sln
├── Directory.Build.props            ← single product version (e.g. 1.0.0) applied to all assemblies
├── appsettings.shared.json          ← single source of truth for all config (InstallRoot, DB, SMTP, Claude, WxVis, Monitor, etc.) — git-tracked
├── Deploy-WxService.ps1             ← PowerShell deploy script (run as Administrator)
├── wgrib2/                          ← runtime-installed, not in repo; operator downloads NOAA native Windows build here
│   ├── wgrib2.exe                   ← Cygwin-compiled NOAA build; path derived from InstallRoot via WxPaths.Wgrib2DefaultPath
│   └── cygwin1.dll                  ← required alongside wgrib2.exe
└── src/
    ├── MetarParser/                 ← METAR text parser library
    ├── TafParser/                   ← TAF text parser library
    ├── GribParser/                  ← wgrib2 subprocess wrapper; CSV parser → GribValue records
    ├── MetarParser.Data/            ← EF Core entities, fetchers, DB context, geocoders, DatabaseSetup
    ├── WxServices.Logging/          ← log4net wrapper (static Logger class)
    ├── WxServices.Common/           ← shared utilities (WxPaths, SmtpSender, SmtpConfig, Util)
    ├── WxInterp/                    ← snapshot interpreter (METAR+TAF+GFS → WeatherSnapshot)
    ├── WxParser.Svc/                ← Windows service: periodic METAR/TAF + GFS fetch
    ├── WxReport.Svc/                ← Windows service: report generation and email
    ├── WxMonitor.Svc/               ← Windows service: log and heartbeat monitoring
    ├── WxVis.Svc/                   ← Windows service: automated map rendering
    ├── WxViewer/                    ← WPF desktop app: animated weather map viewer
    ├── WxManager/                   ← WPF management GUI: recipient editor + announcement sender
    └── WxVis/                       ← Python visualisation project (conda env: wxvis)
        ├── db.py                    ← SQLAlchemy engine + data loading queries
        ├── synoptic_map.py          ← Synoptic analysis maps (Barnes interpolation)
        ├── forecast_map.py          ← GFS forecast parameter maps (contour lines)
        ├── config.json              ← DB connection string + output directory (standalone fallback; service passes env vars)
        └── requirements.txt         ← conda install list
tests/
    ├── MetarParser.Tests/
    ├── TafParser.Tests/
    ├── WxInterp.Tests/
    └── WxMonitor.Tests/
```

WxVis is a standalone Python project; it has no build-time dependency on the C# projects. It reads directly from the same SQL Server database using SQLAlchemy + pyodbc (Windows Authentication).

### Project dependency graph

```mermaid
graph TD
    LOG[WxServices.Logging]
    CMN[WxServices.Common]
    GRIB[GribParser]
    MDATA[MetarParser.Data]
    MP[MetarParser]
    TP[TafParser]
    INTERP[WxInterp]
    PSVC[WxParser.Svc]
    RSVC[WxReport.Svc]
    MSVC[WxMonitor.Svc]
    VSSVC[WxVis.Svc]

    CMN --> LOG
    MDATA --> MP
    MDATA --> TP
    MDATA --> LOG
    MDATA --> GRIB
    INTERP --> MDATA
    PSVC --> MDATA
    PSVC --> LOG
    RSVC --> INTERP
    RSVC --> MDATA
    RSVC --> CMN
    RSVC --> LOG
    MSVC --> CMN
    MSVC --> LOG
    VSSVC --> MDATA
    VSSVC --> LOG
```

---

## 4. Service Details

### 4.1 WxParser.Svc — Data Fetcher

**Purpose:** Keep the local database populated with current METAR, TAF, and GFS forecast data.

**METAR/TAF cycle (default: every 10 minutes):**
1. Resolve home coordinates from config (`HomeLatitude`, `HomeLongitude`). If absent, look up via `AirportLocator` using `HomeIcao` and cache to `appsettings.local.json`.
2. Fetch all METARs within the configured fetch region via the AWC API.  The region is either explicit bounds (`RegionSouth/North/West/East`) or derived from `HomeLatitude/HomeLongitude ± BoundingBoxDegrees`.
3. Fetch the home ICAO station explicitly (in case it falls outside the region).
4. Fetch all TAFs within the same region.
5. Insert new records; skip duplicates (unique index on station + observation time + report type).
6. Write the current UTC timestamp to `wxparser-heartbeat.txt`.

**GFS cycle (default: every 60 minutes):**
1. Check for any incomplete model run registered in `GfsModelRuns`. If one exists, resume it; otherwise compute the most recent GFS cycle (00Z/06Z/12Z/18Z) that should be available on NOMADS.
2. For each forecast hour 0–120 not yet stored, fetch the `.idx` inventory file for that hour. A 404 means the run is still being computed — stop and resume next cycle.
3. Download byte-range HTTP requests for the 8 target variables (TMP, SPFH, UGRD, VGRD, PRATE, TCDC, CAPE, PRMSL) and concatenate them into a temporary GRIB2 file.
4. Invoke `wgrib2.exe` (NOAA native Windows build) to crop to the configured fetch region and emit a CSV of grid values.
5. Assemble `GfsGridPoint` entities (applying unit conversions) and insert into `GfsGrid`.
6. When all 121 hours are stored, mark the run `IsComplete = true` and purge old runs (retaining the 2 most recent).

**Airport metadata refresh cycle (once per week, and on first startup):**
1. Download `airports.csv`, `countries.csv`, and `regions.csv` from OurAirports (`https://davidmegginson.github.io/ourairports-data/`), decoded as UTF-8.
2. Build in-memory lookups from `countries.csv` (alpha-2 code → short name) and `regions.csv` (full ISO 3166-2 code → region name).
3. Parse `airports.csv`; skip rows where `icao_code` and `ident` are both blank, and skip any identifier not exactly 4 characters long.
4. Upsert all valid rows into `WxStations`: update existing rows with properly-cased `Name`, `Municipality`, country fields (`Country`, `CountryCode`, `CountryAbbr`), and region fields (`Region`, `RegionCode`, `RegionAbbr`); insert new rows for airports not yet seen. Coordinates and elevation are refreshed from OurAirports data.

**Metrics emitted (OpenTelemetry):**

| Metric | Type | Description |
|---|---|---|
| `wxparser.fetch.cycles.total` | Counter | Incremented on each successful METAR/TAF fetch cycle |
| `wxparser.fetch.cycle.duration.seconds` | Histogram | Wall-clock duration of each METAR/TAF fetch cycle (buckets: 1 2 5 10 20 30 60 120 s) |
| `wxparser.gfs.cycles.total` | Counter | Incremented on each successful GFS fetch cycle |
| `wxparser.gfs.failures.total` | Counter | Incremented on each failed GFS fetch cycle |
| `wxparser.gfs.cycle.duration.seconds` | Histogram | Wall-clock duration of each GFS fetch cycle (buckets: 30 60 120 300 600 900 1800 s) |

See [Section 11 — Observability](#11-observability) for the collection stack.

**Key classes:**
| Class | Location | Role |
|---|---|---|
| `FetchWorker` | WxParser.Svc | `BackgroundService`; owns the METAR/TAF, GFS, and airport-refresh cycles |
| `MetarFetcher` | MetarParser.Data | AWC API call → parse → insert METARs |
| `TafFetcher` | MetarParser.Data | AWC API call → parse → insert TAFs |
| `GfsFetcher` | MetarParser.Data | NOMADS byte-range download → wgrib2 → insert GfsGridPoints |
| `AirportDataImporter` | MetarParser.Data | Downloads OurAirports CSVs; upserts `WxStations` with names, municipalities, coordinates, and country/region fields |
| `GribExtractor` | GribParser | wgrib2 subprocess wrapper; parses CSV output into `GribValue` records |
| `MetarParser` | MetarParser | Parses raw METAR text into structured objects |
| `TafParser` | TafParser | Parses raw TAF text into structured objects |
| `AirportLocator` | MetarParser.Data | AWC API: resolves ICAO to lat/lon; finds nearest METAR/TAF stations by bounding box |
| `HttpFetchRetry` | MetarParser.Data | Extension method `GetStringWithRetryAsync` wrapping `HttpClient.GetStringAsync` with 3-attempt exponential-backoff retry (2 s → 4 s → 8 s) for transient upstream failures |

**Upstream-fetch error handling:** METAR, TAF, and GFS fetchers all call `HttpFetchRetry.GetStringWithRetryAsync`.  Transient failures (HTTP 5xx, 429, SSL/TLS handshake errors, network-level `IOException`, request-timeout `TaskCanceledException`) are retried up to three times with exponential backoff; each retry logs at `WARN`.  Only when all three attempts have failed does the caller's catch block log at `ERROR`, which is also the point at which WxMonitor's alert pipeline (see WX-25) would escalate.  Permanent failures (4xx other than 429) throw immediately without retry so caller-specific handling still applies — notably `GfsFetcher`'s treatment of HTTP 404/301/302 as "forecast hour not yet published, stop the loop."

---

### 4.2 WxReport.Svc — Report Generator

**Purpose:** Generate personalized weather reports for each recipient and deliver them by email.

**Cycle (default: every 5 minutes):**

```mermaid
flowchart TD
    A[Load config] --> B{Recipients configured?}
    B -->|No| Z[Done]
    B -->|Yes| C[For each recipient]
    C --> D{Resolved?}
    D -->|No| E[Geocode address\nFind nearest METAR + TAF\nCache to Recipients table in DB]
    E --> F{Success?}
    F -->|No| SKIP[Skip recipient]
    D -->|Yes| G[Build WeatherSnapshot]
    F -->|Yes| G
    G --> H[Compute fingerprint]
    H --> I{Should send?}
    I -->|No| C
    I -->|Yes — scheduled / first / change| J[SnapshotDescriber → structured text]
    J --> K[Claude API → report text]
    K --> K2[Attach 48h meteogram PNG if available]
    K2 --> L[Send email with cid: inline image]
    L --> M[Update RecipientState in DB]
    M --> C
```

**Send-decision logic:**
- **First run:** Immediately sends a welcome + weather report.
- **Scheduled:** Sends once per configured hour when that hour arrives in the recipient's local timezone. Multiple hours can be specified (e.g. `"6, 18"` for morning and evening); default is `"7"`. Subject line: "Weather report — …".
- **Significant change:** Sends an unscheduled report when the weather fingerprint changes and the classified severity is `Update` or `Alert`, subject to a minimum gap between sends (default 60 minutes). `Minor` severity changes are suppressed. Subject line and Claude prompt vary by severity:

| Severity | Trigger | Subject line | Claude opening |
|---|---|---|---|
| `Alert` | Dangerous observed-condition flag appeared: wind, visibility, or thunderstorm rose above threshold | "Weather alert — …" | One urgent sentence naming the new condition |
| `Update` | Observed precipitation appeared; GFS risk flag (CAPE or precip) appeared; or forecast high/low temperature bucket shifted | "Weather update — …" | One or two sentences summarising what changed |
| `Minor` | "Conditions improved" — observed flags (wind, visibility, thunderstorm, precipitation) cleared, or GFS risk flags cleared — send suppressed | — send suppressed — | — |

**Significant-change thresholds (configurable):**
| Condition | Default threshold |
|---|---|
| Wind speed | ≥ 25 kt |
| Visibility | < 3.0 SM |
| Ceiling | < 3,000 ft AGL |
| GFS forecast high temperature | Changes by ≥ 15 °F (next calendar day) |
| GFS forecast low temperature | Changes by ≥ 15 °F (next calendar day) |
| GFS CAPE | Any forecast day ≥ 1,000 J/kg |
| GFS precipitation rate | Any forecast day ≥ 2.0 mm/hr |

**METAR station fallback (tiered):**
1. Try each ICAO in the recipient's `MetarIcao` list (comma-separated, preference order); a station is only accepted if its most recent observation is within the last 3 hours.
2. Fall back to the geographically nearest station within 50 km (≈30 statute miles) of the recipient's coordinates that has a METAR in the same 3-hour window. A lat/lon bounding box derived from the radius prefilters candidates; the haversine formula picks the true nearest and enforces the actual radius. Requires recipient coordinates; the fallback is disabled without them.
3. If no station qualifies but a TAF and/or GFS forecast are available, send a forecast-only report with a one-paragraph note in the Current Conditions section explaining that no recent observation is available from a station within ≈30 miles.
4. Only when METAR, TAF, and GFS are all unavailable is the recipient skipped for the cycle.

The config is never updated when a fallback station is used; a warning is logged with the fallback station's distance in miles.  When the station used differs from the one in `RecipientState.LastMetarIcao` (i.e. the station changed since the last report), Claude is informed via the prompt and includes a brief, matter-of-fact note in the report — in the change-summary band for unscheduled sends, or in the closing summary for scheduled ones.  `LastMetarIcao` is updated on every successful send *that carried a real observation*; forecast-only sends leave it untouched so change-detection resumes cleanly once observations return.

Observation-less sends also suppress change-triggered unscheduled sends — the observation portion of the fingerprint would default to "calm / good visibility / no phenomena" and could produce a misleading "conditions cleared!" alert. `RecipientState.LastSnapshotFingerprint` is not updated in that case either, so the next cycle with a real observation compares to the last genuine state.

**Recipient resolution (one-time, cached):**
1. Geocode `Address` via Nominatim → lat/lon + locality name.
2. Query the database for METAR stations active within the last 3 hours; use the AWC bbox API to find the nearest station from that active set (falling back to the full API result on first run before local data exists).
3. Query the AWC TAF bbox API for the nearest TAF station. If none is found, store the sentinel value `"NONE"` to prevent repeated lookups.
4. Write lat, lon, `MetarIcao`, `TafIcao`, and `LocalityName` back to the `Recipients` database table.

**Key classes:**
| Class | Location | Role |
|---|---|---|
| `ReportWorker` | WxReport.Svc | `BackgroundService`; owns the report loop |
| `RecipientResolver` | WxReport.Svc | Address geocoding and station resolution; cache write-back; retries geocoding up to 3× |
| `WxInterpreter` | WxInterp | Queries DB → `WeatherSnapshot` (METAR + TAF + GFS); station fallback logic |
| `GfsInterpreter` | WxInterp | Bilinear interpolation over the four surrounding 0.25° GFS grid points → `GfsForecast` |
| `SnapshotDescriber` | WxReport.Svc | `WeatherSnapshot` → structured plain-text for Claude; unit-aware (temperature, pressure, wind speed); outputs relative humidity (computed from temperature and dew point) rather than raw dew point |
| `ClaudeClient` | WxReport.Svc | Anthropic Messages API wrapper; generates HTML email body; accepts `UnitPreferences` and `ChangeSeverity` to tailor the system prompt per recipient; injects the cached author-persona prefix as the first `system` content block (see *Persona prefix* below); retries transient failures (429, 529, 5xx, `HttpRequestException`) up to 3 times with linear backoff |
| `PersonaPrefix` | WxReport.Svc | Tiny record wrapping the contents of `AboutPaul.md`, loaded once at service startup and threaded into every `ClaudeClient` so it can be sent as a cached system-prompt prefix |
| `SmtpSender` | WxServices.Common | MailKit SMTP wrapper; `SendAsync` accepts optional `htmlBody` and `inlineImages`; sends `multipart/alternative` (plain-text + HTML); HTML part is wrapped in `multipart/related` when inline images are provided (`cid:` URI support); `fromName` set per-service at construction time; all failures (including invalid addresses and SMTP errors) are caught and return `false` rather than throwing |
| `SnapshotFingerprint` | WxReport.Svc | Computes an 8-field pipe-delimited fingerprint (W, V, TS, PR, GH, GL, GC, GP) from significant weather fields; `ClassifyChange` compares two fingerprints and returns a `ChangeSeverity` value |

**Metrics emitted (OpenTelemetry):**

| Metric | Type | Description |
|---|---|---|
| `wxreport.cycles.total` | Counter | Completed report cycles |
| `wxreport.sends.total` | Counter | Reports successfully sent |
| `wxreport.send.failures.total` | Counter | Failed email sends |
| `wxreport.claude.calls.total` | Counter | Claude API calls |
| `wxreport.cycle.duration.seconds` | Histogram | Report cycle duration (buckets: 1 2 5 10 20 30 60 120 s) |
| `wxreport.claude.duration.seconds` | Histogram | Claude API call duration (buckets: 1 2 5 10 15 20 30 60 s) |

**Persona prefix (cached) — `AboutPaul.md`:**

The Anthropic Messages API is stateless: every call begins with no knowledge of who Paul is, what voice he writes in, or what content rules he wants applied. To give Claude that context without paying for it on every call, every `ClaudeClient.GenerateReportAsync` request opens with an author-persona prefix — the full contents of `AboutPaul.md` at the repo root — sent as the first element of the `system` content-block array, with `cache_control: { type: "ephemeral" }` attached.

- **Source of truth.** `HarderWare/AboutPaul.md` (repo root, not under `WxServices/`). The file is curated to be public-safe by design — its top section codifies an explicit inclusion/exclusion rule so the doc can live in a public repo without leaking content unsuitable for customer-facing output. Future HarderWare services that generate voice-bearing output should consume the same file rather than fork their own copy.
- **Deployment.** `WxReport.Svc.csproj` includes the file via `<Content Include="..\..\..\AboutPaul.md"><Link>AboutPaul.md</Link><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></Content>` so it is copied alongside the binary at build time. `Program.cs` reads the deployed copy once at startup via `File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "AboutPaul.md"))`, wraps it in a `PersonaPrefix` record, and registers that as a DI singleton consumed by `ReportWorker`.
- **Fail-fast on missing or blank file.** Startup aborts if `AboutPaul.md` is not on disk, or if it is empty / whitespace-only, or if it cannot be read (I/O error, permission denied): the persona prefix is required for every Claude call, and silently falling back to generic output would be a worse failure mode than refusing to start. Each failure mode logs a path-specific error before the exception propagates.
- **API shape.** The `system` parameter is sent as a two-element array: persona block first (with `cache_control: ephemeral`), dynamic per-recipient prompt second (uncached). A cache breakpoint covers everything up to and including the block it is attached to, so this layout caches the persona alone — per-recipient instructions vary by language, locality, and severity and must remain fresh.
- **Cache eligibility.** Anthropic's minimum cacheable size is 1024 tokens for Sonnet/Opus and 2048 tokens for Haiku. `AboutPaul.md` measures ~2400 tokens, which clears both thresholds — the persona block alone is large enough to engage caching on any current Claude model. The production model is `Claude:Model` in `appsettings.shared.json` (currently `claude-sonnet-4-6`). The first call within a TTL window performs a *cache write* (`cache_creation_input_tokens` ≈ persona size, `cache_read_input_tokens` = 0); subsequent calls within the TTL perform *cache reads* at reduced token cost. Default TTL is 5 minutes (`ephemeral`).
- **Refresh discipline.** Source attribution and refresh policy live in the sibling `AboutPaul.sources.md` at the repo root (kept out of the model-facing prompt by design). Refreshes are drift-triggered (output starts sounding off, or a source memory file changes materially) and flow through the normal Jira-ticket-and-PR workflow so CodeRabbit reviews the diff.

---

### 4.3 WxVis.Svc — Map Renderer

**Purpose:** Automatically render weather maps after each data cycle, eliminating the need to run Python scripts manually.

**Three workers:**

| Worker | Trigger | Output filename format |
|---|---|---|
| `AnalysisMapWorker` | After each METAR fetch cycle; one PNG per zoom level | `synoptic_{label}_{yyyyMMdd_HH}_z{N}.png` |
| `ForecastMapWorker` | Progressively, as each forecast hour's data arrives; one PNG per zoom level | `forecast_{yyyyMMdd_HH}_f{NNN}_z{N}.png` |
| `MeteogramWorker` | Once per complete GFS model run; one pair per unique (ICAO, TempUnit, Timezone) in Recipients | `meteogram_{yyyyMMdd_HH}_{ICAO}_{tzSafe}_{F\|C}_abbrev.png`, `meteogram_{yyyyMMdd_HH}_{ICAO}_{tzSafe}_{F\|C}_full.png`; manifest: `meteogram_manifest_{yyyyMMdd_HH}.json` |

All workers check for existing current output files before invoking Python; already-current files are skipped.

**Map rendering (`MapRenderer`):**
- Invokes the appropriate WxVis Python script via `Process`/`ProcessStartInfo`.
- Augments the process `PATH` with the conda environment's `bin`, `Library\bin`, and `Scripts` directories so Python and its DLL dependencies resolve correctly when the service runs under the Windows service account (which has a minimal PATH).
- Captures stdout and stderr separately: stdout lines are logged at INFO; stderr lines are logged at WARN (so genuine Python tracebacks surface as warnings). WxVis's `logger.py` directs its console handler to `sys.stdout` so that normal Python log output does not trigger spurious WARN entries in the service log.
- On cancellation (service stop or redeploy), kills the Python subprocess via `Kill(entireProcessTree: true)` before re-throwing, preventing orphaned render processes from running after the service exits.
- All three Python scripts (`forecast_map.py`, `synoptic_map.py`, `meteogram.py`) write output to a `.tmp` file and atomically rename it to the final `.png` via `os.replace()`. This ensures the output directory never contains a partially-written image, so WxVis.Svc is safe to stop and redeploy at any time without risk of serving corrupt maps. Each `plt.savefig()` call explicitly passes `format="png"` because matplotlib infers the output format from the file extension — without it, `.png.tmp` would be treated as an unknown format and the render would fail.

**Stale plot cleanup:** `AnalysisMapWorker` runs a daily purge that deletes `*.png` files older than `PlotRetentionDays` from the output directory.

**Key classes:**
| Class | Location | Role |
|---|---|---|
| `AnalysisMapWorker` | WxVis.Svc | `BackgroundService`; renders synoptic maps after METAR cycles; daily PNG purge |
| `ForecastMapWorker` | WxVis.Svc | `BackgroundService`; renders forecast hours progressively as data arrives for the latest model run (complete or still ingesting) |
| `MeteogramWorker` | WxVis.Svc | `BackgroundService`; renders a 48h abbreviated + full-period meteogram for each recipient location after each complete GFS run; writes manifest JSON |
| `MapRenderer` | WxVis.Svc | Subprocess launcher; conda PATH augmentation; stdout/stderr capture |

**Metrics emitted (OpenTelemetry):**

| Metric | Type | Description |
|---|---|---|
| `wxvis.analysis.renders.total` | Counter | Completed analysis map renders |
| `wxvis.analysis.failures.total` | Counter | Failed analysis map renders |
| `wxvis.forecast.renders.total` | Counter | Completed forecast frame renders |
| `wxvis.forecast.failures.total` | Counter | Failed forecast frame renders |
| `wxvis.render.duration.seconds` | Histogram | Render duration (buckets: 5 10 20 30 60 120 300 s); tagged with `map_type` |

---

### 4.4 WxVis — Python Visualisation

**Purpose:** Render weather maps from the local database. Called automatically by WxVis.Svc; can also be run manually for development and testing.

**Script catalogue:**

| Script | Output type | Data source | Output filename |
|---|---|---|---|
| `synoptic_map.py` | Synoptic analysis map (Barnes interpolation) | Latest METAR + WxStations | `synoptic_{label}_{yyyyMMdd_HH}_z{N}.png` |
| `forecast_map.py` | GFS forecast parameter map | GfsGrid for a specific model run and forecast hour | `forecast_{yyyyMMdd_HH}_f{NNN}_z{N}.png` |
| `meteogram.py` | Point-forecast meteogram (two PNGs per location) | GfsGrid nearest grid point; bilinear interpolation to recipient lat/lon | `meteogram_{yyyyMMdd_HH}_{ICAO}_{tzSafe}_{F\|C}_abbrev.png`, `meteogram_{yyyyMMdd_HH}_{ICAO}_{tzSafe}_{F\|C}_full.png` |

**Rendering details:**
- Map projection is selected automatically by `choose_projection()` based on the centre latitude of the extent: Mercator for tropics (|lat| < 25°), Lambert Conformal for mid-latitudes (25–70°), Stereographic for polar regions (≥ 70°).
- Map limits are computed by dense boundary sampling (`_inner_proj_limits`, 200 points per edge) so the plotted area fills to the border with no empty corners from projection curvature.
- Isobars: black solid, 4 hPa interval, labelled.
- Temperature isopleths: red dashed, 3°C interval, labelled.
- Dewpoint isopleths: teal (`#00838f`) dashed, 3°C interval, labelled. Teal distinguishes isodrosotherms from the green precipitation fill.
- Precipitation shading (forecast_map only): semi-transparent green (`#66bb6a`, alpha 0.45) `contourf` fill over areas where the Gaussian-smoothed GFS PRATE field exceeds 0.1 mm/hr. Drawn below isopleths so contour lines and station symbols remain legible. The smoothing turns the blocky 0.25° grid into a smooth curved boundary.
- Pressure extrema: **H** (navy) / **L** (maroon), neighbourhood 12 grid cells (~3°/333 km), minimum prominence 1 hPa.
- Temperature extrema: **W** (dark red) / **K** (steel blue), neighbourhood 12 grid cells, no minimum prominence filter.
- Station models (synoptic_map): MetPy StationPlot; stations thinned with `reduce_point_density` (default 75 km). Fields plotted: NW = air temperature (dark red), SW = dew point (dark green), NE = encoded SLP (3-digit), centre = wind barb + sky-cover symbol + present-weather symbol, SE = station ICAO ID (navy).
- Station models (forecast_map): MetPy StationPlot at METAR station locations, displaying interpolated GFS values. Fields plotted: NW = air temperature (dark red), SW = dew point (dark green), NE = encoded SLP (3-digit), centre = wind barb + sky-cover symbol, SE = station ICAO ID (navy).
- Contours (synoptic_map): Barnes-interpolated grid converted from projection metres to lat/lon before plotting so Cartopy clips to the inner viewport, matching forecast_map white-space border behaviour.
- MSLP from METAR (synoptic_map): the altimeter setting is QNH (already reduced to MSL using the ISA temperature profile), so it is converted to station pressure via the ISA polytropic formula and then back to MSLP via the hypsometric equation using the station's own air temperature and a 6.5 K/km mean-layer lapse. Falls back to QNH when the station is at or below mean sea level, when station elevation is missing or non-finite, or when station temperature is missing or non-finite.
- Map extent is configured via `WxVis:MapExtent` in `appsettings.shared.json`. Accepts a preset name (`south_central`, `conus`) or explicit W,E,S,N coordinates (e.g. `"-106,-88,25,38"`). When empty, maps auto-fit to the available data.  `CONUS_EXTENT` is `(-136, -60, 17, 55)`.
- **Multi-zoom rendering:** `WxVis:ZoomLevels` (default 3) controls how many zoom levels are rendered per map. Each level doubles the figure size (`11" × 2^(N-1)`) and halves the station density (`150 km / 2^(N-1)`). Font sizes and line widths scale by `sqrt(2^(N-1))` to maintain visual proportion. DPI is 150 for z1 and 100 for z2+. Contour intervals are fixed at 8 hPa (isobars) and 5°C (isotherms) across all levels. Dewpoint isopleths are suppressed at z1. Both `synoptic_map.py` and `forecast_map.py` accept `--zoom-level N`; station density thinning via `reduce_point_density` is applied in both scripts.
- Extrema labels (H/L/W/K): before placing a label, its lat/lon position is converted to projection metres and compared against `ax.get_xlim()`/`ax.get_ylim()` with a 3 % inward margin on all edges; labels outside or too close to the boundary are silently skipped. The margin guards against `plt.tight_layout()`, which adjusts subplot padding after labels are placed and can shift the effective axes boundary enough to push a borderline anchor outside the saved image. `ax.set_xlim`/`ax.set_ylim` are also re-applied after `tight_layout()` for the same reason.

**Meteogram (`meteogram.py`):**
- Loaded via `db.load_gfs_nearby()` — queries GfsGrid within ±0.5° of the target lat/lon for all forecast hours of the run, then selects the nearest grid point per hour.
- Two vertical panels: top (1/3 height) = wind barbs (always in knots); bottom (2/3 height) = temperature line (black, left axis) and relative humidity line (green, right axis, 0–100%).
- Left axis: "T (°F)" or "T (°C)" depending on `--temp-unit`.  Right axis: "RH (%)" with tick labels rendered in green to match the RH line and axis label.
- Thin horizontal grid lines in the bottom panel at each temperature-axis tick position (light grey, `linewidth=0.4`); anchored to the temperature axis so tick labels are always round numbers. RH axis grid suppressed to avoid a second overlapping set of lines.
- Time axis in recipient local time (`--tz`, IANA timezone name, e.g. `America/Chicago`). Bold vertical lines at every local midnight; day-of-week and day-of-month labels centred in each day's segment. X-axis ticks every 6 local hours, labelled HH:MM.
- Barbs thinned automatically if spacing < 0.18" to prevent overlapping.
- Abbreviated version (48-hour, emailed): first 48 hours, 10" wide × 3.0" @ 100 dpi → 1000 × 300 px.
- Full-period version: all available hours, width scales with duration (10"–18") × 3.0" @ 100 dpi.
- RH computed from TmpC and DwpC via Magnus formula.  Wind speed converted m/s → kt.
- `tzSafe` in output filenames = IANA name with `/` replaced by `-` (e.g. `America-Chicago`).

**Manual use:**
```powershell
conda activate wxvis
cd C:\Users\PaulH\...\WxServices\src\WxVis

python synoptic_map.py [--extent south_central] [--density 150] [--zoom-level 1]
python forecast_map.py --run 20260402_18 --fh 84 [--extent -106,-88,25,38] [--zoom-level 2]
python meteogram.py --run 20260404_00 --lat 29.97 --lon -95.34 --icao KDWH \
    --locality "Spring" --temp-unit F --tz "America/Chicago" \
    --out-abbrev C:\HarderWare\plots\meteogram_20260404_00_KDWH_America-Chicago_F_abbrev.png \
    --out-full C:\HarderWare\plots\meteogram_20260404_00_KDWH_America-Chicago_F_full.png
# Chart title: "Spring (°F)"  — locality name and unit only; no ICAO prefix
```

Output PNGs are saved to the directory configured in `config.json` (default `C:\HarderWare\plots\`).

**Key files:**
| File | Role |
|---|---|
| `db.py` | SQLAlchemy engine; `load_latest_metars`, `load_gfs_grid`, `load_gfs_nearby` (bounding-box query for meteogram point interpolation), `load_output_dir` |
| `map_utils.py` | Shared rendering utilities: `CONUS_EXTENT`, `SOUTH_CENTRAL_EXTENT`, `_inner_proj_limits`, `_mark_extrema`, `_smooth_with_nans` |
| `synoptic_map.py` | METAR data-prep helpers; Barnes contour analysis; `render_synoptic_map` |
| `forecast_map.py` | GFS grid contouring; `render_forecast_map` |
| `meteogram.py` | Point-forecast meteogram; `_nearest_point_series`, `_compute_rh`, `render_meteogram` |

---

### 4.5 WxMonitor.Svc — Health Monitor

**Purpose:** Alert the operator by email when either watched service logs errors, goes silent, or METAR data goes stale.

**Cycle (default: every 5 minutes):**
1. For each watched service, scan its log file for entries at or above `AlertOnSeverity` (default: ERROR) with a timestamp newer than the last one processed.
2. For each watched service, read its heartbeat file and compare its age to `HeartbeatMaxAgeMinutes`.
3. Query the database for the most recent METAR observation timestamp; if it is older than `MetarStalenessThresholdMinutes` (default 120), send a staleness alert.
4. Send alert emails for any findings not on cooldown (`AlertCooldownMinutes`, default 60).
5. Persist state (last-seen log timestamp, last-alert timestamps) to `wxmonitor-state.json`.

**First-run behaviour:** On first run, `LastSeenLogTimestamp` is null. The scanner baselines to the latest entry in the log without sending alerts, so installation does not flood the inbox with historical errors.

**Key classes:**
| Class | Location | Role |
|---|---|---|
| `MonitorWorker` | WxMonitor.Svc | `BackgroundService`; owns the monitor loop |
| `LogScanner` | WxMonitor.Svc | Parses log file; handles multi-line entries (stack traces); filters by severity and timestamp |
| `HeartbeatChecker` | WxMonitor.Svc | Reads heartbeat file; returns age |
| `SmtpSender` | WxServices.Common | MailKit SMTP wrapper; `fromName` set per-service at construction time |
| `MonitorStateStore` | WxMonitor.Svc | Reads/writes `wxmonitor-state.json` |

**Metrics emitted (OpenTelemetry):**

| Metric | Type | Description |
|---|---|---|
| `wxmonitor.cycles.total` | Counter | Completed monitor cycles |
| `wxmonitor.alerts.total` | Counter | Alert emails sent |

---

### 4.6 WxViewer — Desktop Map Viewer

**Purpose:** Provide a local WPF desktop application for browsing and animating the PNG maps and meteograms produced by WxVis.Svc.

**Layout:** A frameless maximised window (no OS title bar) with a custom header bar containing the HarderWare/WxViewer logo and standard window controls (minimise, restore, close). Below the header, a `TabControl` hosts two tabs:

**Maps tab** — split into two independent panes by a draggable `GridSplitter`:

| Pane | Content | Controls |
|---|---|---|
| Left | Synoptic analysis maps | Map selector (by obs time), step back/play/step forward, speed, time slider, obs-time label, zoom level indicator (Z1/Z2/Z3), Reset Zoom, Link Panes toggle |
| Right | GFS forecast maps | Run selector, step back/play/step forward, speed, hour slider, valid-time label, zoom level indicator, Reset Zoom |

Each pane has its own toolbar docked to the top of the pane, immediately above the map image. Maps support multi-zoom with mouse-wheel zoom, click-drag pan, and automatic image swapping at zoom thresholds. The **Link Panes** toggle (on by default, green when active) synchronises zoom and pan between both panes. **Reset Zoom** (or double-click the map) returns to fit-to-window at zoom level 1. The zoom level indicator (e.g. "Z2") updates live. Each zoom level is pre-rendered at progressively higher resolution by the workers (configured via `WxVis:ZoomLevels`). During active zoom/pan, bitmap scaling switches to low-quality for responsiveness, restoring high-quality after 200 ms of inactivity. A brief crossfade animation smooths zoom-level transitions.

**Meteograms tab** — shows full-period meteograms for a selected GFS run:
- Run selector ComboBox (newest first).
- Recipient selector ComboBox (next to the Run selector) — lists all recipients from the database as `"recipientId — Name (Language)"`. Selecting a recipient scrolls to their meteogram and briefly highlights it with a coloured background (clears after 2 seconds). If no meteogram exists for the recipient in the current run a modal dialog is shown. Matching uses `(FirstIcao, TempUnit, Timezone)` — the same grouping key used by `MeteogramWorker`. `FindMeteogramAbbrevPath` also filters by `TempUnit` so each recipient gets the meteogram rendered in their configured temperature unit.
- Vertically scrollable list of locations sorted by ICAO, each labelled `"KXXX — Locality (°F) · City"` where *City* is the city component of the IANA timezone (e.g. `· Chicago`). Multiple entries for the same ICAO are possible when recipients share a station but use different timezones or temperature units.
- Each meteogram item has a **Recipients** button (left of the label) that opens a modal dialog listing every recipient who receives that meteogram: ID, Name, and Language.
- Each meteogram image is independently horizontally scrollable (full-period images can be 1800 px wide).
- Populated from `meteogram_manifest_{yyyyMMdd_HH}.json` files written by `MeteogramWorker`. Each manifest entry carries `Icao`, `LocalityName`, `TempUnit`, `Timezone`, `FileAbbrev`, and `FileFull`.

**File discovery (`MapFileScanner`):**
- Scans the configured output directory for `synoptic_*_z*.png`, `forecast_*_z*.png`, and `meteogram_manifest_*.json` files on startup and whenever the directory changes.
- Two `FileSystemWatcher` instances: one for `*.png`, one for `*.json`.
- Parses the timestamp and zoom level embedded in each filename. Analysis and forecast files are grouped by observation/run time, collecting all zoom-level variants into a `ZoomPaths` dictionary on each `AnalysisMap` or `ForecastFrame` record. Entries are sorted newest-first.
- `DirectoryChanged` events are marshalled back to the WPF UI thread via `Dispatcher.BeginInvoke`.

**Animation:**
- Two independent `DispatcherTimer` instances — one per pane — allow both panes to play simultaneously at their own speeds.
- Analysis pane defaults to the newest map; the slider and ComboBox stay in sync — selecting a map from either updates the other. Play animates oldest-to-newest; if already at the newest it restarts from the oldest. The analysis slider uses `IsDirectionReversed="True"` so the thumb sits at the right for the newest map and moves left toward older observations.
- Forecast pane starts at forecast hour 0; play steps through all available hours.
- `BitmapImage` is loaded with `CacheOption.OnLoad` (releases file handle immediately) and `Freeze()`d for cross-thread safety.

**Key classes:**
| Class | Role |
|---|---|
| `MapFileScanner` | Directory scan + two `FileSystemWatcher`s (PNG + JSON); returns `List<AnalysisLabel>`, `List<ForecastRun>`, `List<MeteogramRun>` |
| `AnalysisLabel` | Represents one analysis PNG file; `Name` = file path (selection key), `Label` = obs-time string |
| `MeteogramRun` | One GFS run with a list of `MeteogramItem`s parsed from the manifest JSON |
| `MeteogramItem` | One location entry; loads and freezes `FullImage` (`BitmapImage`) on construction |
| `MainViewModel` | All bindable state; two animation timers; meteogram run/item collections; `Refresh()` scans all three file types |
| `RelayCommand` | `ICommand` implementation; `CanExecuteChanged` wired to `CommandManager.RequerySuggested` |
| `MainWindow` | Frameless WPF window; `WindowChrome` for correct maximise/resize behaviour; custom title-bar and keyboard event handlers; suppresses ToolBar gripper and overflow button |

**Keyboard navigation:** Arrow keys are handled globally by `MainWindow.OnKeyDown`. Both sliders have `Focusable="False"` so they cannot capture keyboard focus and interfere with arrow-key routing; mouse dragging still works normally. ComboBoxes return keyboard focus to the window via `DropDownClosed`, so arrow-key navigation resumes immediately after a selection is made; it is suppressed only while a dropdown is open.

| Key | Action |
|---|---|
| `→` / `←` | Forecast: step forward / back one hour |
| `Ctrl+→` / `Ctrl+←` | Forecast: jump to final / first hour |
| `↑` / `↓` | Analysis: step to newer / older map |
| `Ctrl+↑` / `Ctrl+↓` | Analysis: jump to newest / oldest map |

**Logging and error handling:** WxViewer uses the same `WxServices.Logging` static `Logger` as the services. Startup and exit are logged. Three global exception handlers are registered at startup: `DispatcherUnhandledException` (WPF UI thread), `AppDomain.CurrentDomain.UnhandledException` (non-UI threads), and `TaskScheduler.UnobservedTaskException` (fire-and-forget tasks). All catch blocks that previously swallowed exceptions now log via `Logger.Warn` — this covers settings parsing, database queries, image loading, manifest parsing, and `FileSystemWatcher` errors. `MainWindow.OnClosed` unsubscribes ViewModel event handlers and stops the quality-restoration timer. `MainViewModel.Dispose` stops all timers and unsubscribes from `MapFileScanner.DirectoryChanged`. The highlight timer is created once and reused rather than recreated on each highlight.

**Configuration:** The plots directory is derived from `InstallRoot` via `WxPaths`. Override `WxVis:PlotRetentionDays` in `appsettings.shared.json` to control how long plot files are retained.

---

### 4.7 WxManager — Management GUI

**Purpose:** WPF desktop application that provides a tabbed GUI for managing the WxServices system. Deployed to `C:\HarderWare\WxManager`.

**Tabs:**

- **Setup** — Prerequisites checklist that runs on application load. Uses `PrerequisiteChecker` from `WxServices.Common` to verify SQL Server, wgrib2, conda Python, wxvis packages, and Docker (optional). Each check shows a pass/fail indicator with a status message; failed checks include guidance text. A "Re-check" button re-runs all checks. The Configure, Recipients, and Announcement tabs are disabled until all required prerequisites pass.

- **Configure** — Settings editor for the entire system. Pre-populates all fields from the current configuration. Grouped into panels: Paths (InstallRoot, CondaPythonExe, wgrib2.exe path), Home Location (ICAO, lat/lon, bounding box), Database (connection string + Test button), Email/SMTP (host, port, credentials + Test button that sends a real email), Claude API (key, model + Test button that sends a minimal API request), Map Rendering (extent preset or coordinates), and Monitoring (alert email). Saves all settings to `{InstallRoot}\appsettings.local.json`, which is in every service's config chain as an override layer — no redeployment needed. After save, automatically switches to the Setup tab and re-runs prerequisite checks.

- **Recipients** — Left pane shows a scrollable list of all recipients from the `Recipients` database table. Right pane provides an address-input field (three accepted forms — see below), a nearby-stations grid, and a full recipient field editor. Selecting a station pre-fills the MetarIcao field.

  **Address input forms.** `AddressGeocoder.LookupAsync` dispatches based on the leading characters of the input, in this order: (1) `///word.word.word` is treated as a What3Words address and resolved via the public `convert-to-coordinates` API using `What3Words:ApiKey` from `appsettings.local.json`; (2) two decimal-degree numbers separated by a comma (e.g. `30.07, -95.55`) are parsed locally with no API call (the user fills in Locality manually); (3) anything else is passed to the Nominatim (OpenStreetMap) geocoder, which returns a coordinate pair plus a locality derived from the most-specific available place name. All three paths feed into the same nearby-stations lookup flow described below. Latitude and Longitude remain read-only — direct coordinate entry is reached via the `lat, lon` form of the Address field, so it always passes through the same nearby-stations pipeline. Locality, METAR ICAO, and TAF ICAO are directly editable so the user can correct individual fields without re-running geocoding; Save's existing ICAO and range validation remains the gate.

  **Nearby-stations lookup:** After geocoding, WxManager queries `WxStations` for up to `WxManager:MaxNearbyStationsInLookup` (default 40) nearest known stations within `WxManager:StationLookupRadiusKm` (default 150 km), using a lat/lon bbox pre-filter followed by a Haversine sort. For each candidate it counts local METAR and TAF records. If a station has no local METAR records, it issues a single-station AWC query (lookback window: `WxManager:AwcMetarHours`, default 6 hours) as a fallback; stations that respond there but not in bbox results are flagged `AlwaysFetchDirect = true` in the database so the fetch cycle fetches them individually going forward. Non-reporting stations (no local data and no AWC response) are suppressed; up to `WxManager:MaxDisplayStations` (default 5) active stations are shown. A "Searching…" advisory is displayed in the Nearby Stations panel while the queries run. A successful address geocode implicitly begins editing (enabling Save/Cancel without requiring an explicit New click). Save validates all fields before writing: Id must be non-empty and contain only letters, digits, hyphens, and underscores; Email must be a valid RFC 5321 address (`System.Net.Mail.MailAddress`); Timezone must be a recognised IANA ID (from `BuildIanaTimeZoneList`); ScheduledSendHours, if set, must be comma-separated integers in 0–23; MetarIcao and TafIcao tokens must be exactly 4 alphanumeric characters and must exist in the `WxStations` table with non-null coordinates (hard block); if a station's coordinates fall outside the configured fetch bounding box the save proceeds but an amber warning banner is shown in place of the green success banner. Save writes directly to the `Recipients` table and shows a green auto-dismissing banner ("Saved successfully.") on success — the banner dismisses after `WxManager:SuccessMessageDismissMs` (default 3000 ms) — or an amber persistent banner on validation failure or bounding-box warning. Delete removes the row after confirmation and returns the form to an idle state (all fields blank, Save/Cancel/Delete disabled). A **Cancel** button discards unsaved edits at any time. The Timezone field is an editable ComboBox populated with canonical IANA timezone IDs (via `TimeZoneInfo.TryConvertWindowsIdToIanaId`); typing narrows the jump target. Default for new recipients: Language = `Report:DefaultLanguage` (typically "English"), Timezone = `WxManager:DefaultTimezone` (default "America/Chicago"), ScheduledSendHours = `Report:DefaultScheduledSendHour` (default 7), Temperature = °F, Pressure = inHg, Wind = mph.

- **Announcement** — Multi-line text editor for composing operator service announcements. Clicking **Send** loads the recipient list from the database, groups recipients by language, calls Claude to format the announcement as a professional HTML email for each language group (translating non-English groups), and sends via SMTP. Progress is shown inline. On complete success the text area is cleared; partial failures are reported in a dismissible amber message panel with selectable text.

**Configuration:** Follows the same layered pattern as the services. `appsettings.shared.json` supplies settings shared across projects (`Smtp`, `Claude`, `Fetch`, `Report:DefaultLanguage`, `Report:DefaultScheduledSendHour`). WxManager-specific non-secret settings (`WxManager:` section — station lookup radius, display limits, default timezone, AWC endpoint, User-Agent, success-banner timing) live in WxManager's own `appsettings.json`. Most secrets (`Claude:ApiKey`, `Smtp:Username`, `Smtp:Password`, `Smtp:FromAddress`) are read from the `GlobalSettings` database row (Id = 1), with `C:\HarderWare\appsettings.local.json` and a local `appsettings.local.json` beside the executable as fallbacks. `What3Words:ApiKey` is currently the file-based exception, configured directly in `appsettings.local.json`.

**Deploy:** `.\Deploy-WxService.ps1 WxManager` publishes to `C:\HarderWare\WxManager`.

---

## 5. Class Libraries

### WxServices.Logging

A thin static wrapper around log4net. All services, WxManager, and WxViewer call `Logger.Initialise(logFilePath)` once at startup, passing the full log file path derived from `WxPaths`; thereafter `Logger.Info/Warn/Error/Fatal` are available everywhere. Caller file, method, and line number are captured automatically via `[CallerFilePath]` etc.

A single `log4net.shared.config` in the solution root is shared by all components. It uses `%property{LogFile}` (a log4net `PatternString`) to resolve the log file path set by `Logger.Initialise` at runtime. This replaces the former per-service `log4net.config` files.

Log format: `yyyy-MM-dd HH:mm:ss.fff LEVEL [File::Method:Line] message`

ReportWorker log messages that refer to a specific recipient are prefixed with `{Id} {Email} ({Name})` — e.g. `pablo_es PaulHarder2@gmail.com (Pablo): generating scheduled report.` The GFS forecast summary also logs temperatures in the recipient's configured unit (°F or °C).

All timestamps are UTC: the shared config uses `%utcdate`, and the Python logger uses `time.gmtime`. `LogScanner` parses these timestamps with `DateTimeStyles.AssumeUniversal`.

### WxServices.Common

Shared utility code referenced by all services and applications.

Key types:
- `WxPaths` — derives all standard directory paths (Logs, plots, temp, WxVis, services, etc.) from a single `InstallRoot` setting; provides `ReadInstallRoot()` to bootstrap the value from `appsettings.shared.json` before the configuration builder runs
- `PrerequisiteChecker` — static class with individual async check methods for system prerequisites (SQL Server, wgrib2, conda Python, wxvis packages, Docker); each returns a `CheckResult(Ok, Message)` record. Services call `LogPrerequisitesAsync` at startup to log warnings for failed checks; WxManager uses the individual methods to display an interactive checklist in the Setup tab. (WSL was retired as a prerequisite in WX-33 when `wgrib2` switched from a WSL-invoked Linux binary to the native Windows build.)
- `SmtpConfig` — POCO holding SMTP host, port, credentials, and sender address (no `FromName`; each service supplies its own display name at construction time)
- `SmtpSender` — MailKit-based SMTP wrapper; constructed with `SmtpConfig` and a `fromName` string; `SendAsync` accepts a plain-text body, an optional `htmlBody`, and an optional `inlineImages` dictionary (content-id → file path); when HTML is provided the message is sent as `multipart/alternative`; when inline images are supplied the HTML part is wrapped in `multipart/related` so `<img src="cid:...">` references resolve correctly in email clients; all failures (including invalid addresses and SMTP errors) are caught and return `false` rather than throwing
- `LanguageHelper` — maps natural-language names (English or native script) to BCP 47 IETF tags via `CultureInfo.GetCultures`; also provides localised announcement email subject lines
- `Util` — static utility class; currently exposes `Ignore(object? obj = null)` for suppressing "unused variable" warnings during debugging sessions

### WxInterp

Translates raw database entities into a language-neutral `WeatherSnapshot` value object, and exposes static helpers to find the nearest METAR/TAF station in the database to a given coordinate.

Key types:
- `WeatherSnapshot` — current conditions + TAF forecast periods + optional `GfsForecast`; includes `StationMunicipality` and `StationName` (from `WxStations`) used to label the email report sections
- `WxInterpreter` — queries DB, applies unit conversions, builds snapshot; optionally attaches GFS forecast when lat/lon provided
- `GfsInterpreter` — queries `GfsGrid` for the most recent complete model run and bilinearly interpolates the four surrounding 0.25° grid points to an exact location; produces a `GfsForecast` covering up to 6 days
- `GfsForecast` — model run time + list of `GfsDailyForecast`
- `GfsDailyForecast` — per-day high/low temperature (°C and °F), max wind speed (kt) and dominant direction, max cloud cover (%), max CAPE (J/kg), max precipitation rate (mm/hr above threshold)
- `ForecastPeriod`, `SkyLayer`, `SnapshotWeather` — snapshot sub-types for METAR/TAF data

---

## 6. Data Model

### Entity-relationship diagram

```mermaid
erDiagram
    Metars {
        int Id PK
        string ReportType
        string StationIcao
        datetime ObservationUtc
        bool IsAuto
        int WindDirection
        int WindSpeed
        int WindGust
        string WindUnit
        bool VisibilityCavok
        double VisibilityStatuteMiles
        int VisibilityM
        double AirTemperatureCelsius
        double DewPointCelsius
        double AltimeterValue
        string AltimeterUnit
        string RawReport
        datetime ReceivedUtc
    }

    MetarSkyConditions {
        int Id PK
        int MetarId FK
        string Cover
        int HeightFeet
        string CloudType
        bool IsVerticalVisibility
        int SortOrder
    }

    MetarWeatherPhenomena {
        int Id PK
        int MetarId FK
        string PhenomenonKind
        string Intensity
        string Descriptor
        string Precipitation
        string Obscuration
        string OtherPhenomenon
        int SortOrder
    }

    MetarRunwayVisualRanges {
        int Id PK
        int MetarId FK
        string Runway
        int RvrFeet
        string Trend
        int SortOrder
    }

    Tafs {
        int Id PK
        string ReportType
        string StationIcao
        datetime IssuanceUtc
        datetime ValidFromUtc
        datetime ValidToUtc
        string RawReport
        datetime ReceivedUtc
    }

    TafChangePeriods {
        int Id PK
        int TafId FK
        string ChangeType
        datetime ValidFromUtc
        datetime ValidToUtc
        int WindDirection
        int WindSpeed
        int WindGust
        string WindUnit
        bool VisibilityCavok
        double VisibilityStatuteMiles
        int VisibilityM
        int SortOrder
    }

    TafChangePeriodSkyConditions {
        int Id PK
        int TafChangePeriodId FK
        string Cover
        int HeightFeet
        string CloudType
        bool IsVerticalVisibility
        int SortOrder
    }

    TafChangePeriodWeatherPhenomena {
        int Id PK
        int TafChangePeriodId FK
        string Intensity
        string Descriptor
        string Precipitation
        string Obscuration
        string OtherPhenomenon
        int SortOrder
    }

    Recipients {
        int Id PK
        string RecipientId UK
        string Email
        string Name
        string Language
        string Timezone
        string ScheduledSendHours
        string Address
        string LocalityName
        float Latitude
        float Longitude
        string MetarIcao
        string TafIcao
        string TempUnit
        string PressureUnit
        string WindSpeedUnit
    }

    GlobalSettings {
        int Id PK
        string ClaudeApiKey
        string SmtpUsername
        string SmtpPassword
        string SmtpFromAddress
    }

    RecipientStates {
        int Id PK
        string RecipientId UK
        datetime LastScheduledSentUtc
        datetime LastUnscheduledSentUtc
        string LastSnapshotFingerprint
        string LastMetarIcao
    }

    GfsModelRuns {
        datetime ModelRunUtc PK
        bool IsComplete
    }

    GfsGrid {
        int Id PK
        datetime ModelRunUtc FK
        int ForecastHour
        float Lat
        float Lon
        float TmpC
        float DwpC
        float UGrdMs
        float VGrdMs
        float PRateKgM2s
        float TcdcPct
        float CapeJKg
        float PrMslPa
    }

    WxStations {
        char IcaoId PK
        nvarchar Name
        nvarchar Municipality
        float Lat
        float Lon
        float ElevationFt
        bit AlwaysFetchDirect
        nvarchar Region
        nvarchar RegionCode
        nvarchar RegionAbbr
        nvarchar Country
        nchar CountryCode
        nvarchar CountryAbbr
    }

    Metars ||--|| WxStations : "station metadata"
    Metars ||--o{ MetarSkyConditions : "has"
    Metars ||--o{ MetarWeatherPhenomena : "has"
    Metars ||--o{ MetarRunwayVisualRanges : "has"
    Tafs ||--o{ TafChangePeriods : "has"
    TafChangePeriods ||--o{ TafChangePeriodSkyConditions : "has"
    TafChangePeriods ||--o{ TafChangePeriodWeatherPhenomena : "has"
    GfsModelRuns ||--o{ GfsGrid : "has"
```

### Key indexes

| Table | Index | Type |
|---|---|---|
| Metars | StationIcao + ObservationUtc + ReportType | Unique |
| Metars | StationIcao | Non-unique |
| Tafs | StationIcao + IssuanceUtc + ReportType | Unique |
| Tafs | StationIcao | Non-unique |
| Recipients | RecipientId | Unique |
| RecipientStates | RecipientId | Unique |
| GfsGrid | ModelRunUtc + ForecastHour + Lat + Lon | Unique |
| GfsGrid | ModelRunUtc + ForecastHour | Non-unique |
| WxStations | IcaoId | Primary key |

**Notes on WxStations:**
- Seeded from OurAirports (`airports.csv`, `countries.csv`, `regions.csv`) by `AirportDataImporter`, which runs on first startup and weekly thereafter. Populates `Name` (properly cased), `Municipality` (city/town), coordinates, and the country/region fields for all ~40 000 ICAO-coded airports worldwide.
- `MetarFetcher` also inserts stub rows for any ICAO not yet present after a METAR batch, so new stations appear immediately even before the next weekly refresh.
- `Municipality` is used by `WxReport.Svc` to label the Current Conditions section with a human-readable location rather than an ICAO code.
- Country / region columns are derived from OurAirports' `iso_country` and `iso_region`:
  - `CountryCode` is the ISO 3166-1 alpha-2 code (e.g. `US`, `GB`). `Country` is the short name from `countries.csv` (e.g. "United States", "United Kingdom"). `CountryAbbr` is a display-friendly override maintained in `AirportDataImporter.CountryAbbrOverrides` (currently `GB`→`UK`, `US`→`USA`); defaults to `CountryCode` otherwise.
  - `RegionCode` is the full ISO 3166-2 subdivision code (e.g. `US-TX`, `GB-ENG`). `RegionAbbr` is the portion after the hyphen (e.g. `TX`, `ENG`). `Region` is the subdivision name from `regions.csv` (e.g. "Texas", "England").
- The WxManager Nearby Stations grid displays `"{Municipality}, {RegionAbbr}, {CountryAbbr}"` (e.g. `Brenham, TX, USA`), falling back to the airport name when location parts are unavailable.
- WxVis queries `WxStations` via `INNER JOIN` so stub rows (null coordinates) are automatically excluded from maps.

---

## 7. Configuration Guide

### File layering

Each service loads configuration from up to two files, merged in order (later file wins):

| File | Tracked by git | Purpose |
|---|---|---|
| `appsettings.shared.json` | Yes | All non-secret settings for every service and application |
| `appsettings.local.json` | **No** | Per-machine overrides written by WxManager → Configure tab |

Most secrets (SMTP credentials, Claude API key) are stored in the `GlobalSettings` database row (Id = 1) and never appear in any configuration file. `What3Words:ApiKey` is currently the file-based exception (in `appsettings.local.json`). Use WxManager → Configure tab to set the database-stored secrets.

### appsettings.shared.json (WxServices root)

This single file contains every non-secret setting for all services and applications. Key sections:

- **`Fetch`** — METAR/TAF fetch interval, home station, bounding box / explicit region bounds, retention
- **`Gfs`** — GFS fetch interval, delay, wgrib2 path, forecast hours, retention
- **`Smtp`** — Host and port only (credentials are in the database)
- **`Claude`** — Model, endpoint, API version, max tokens (API key is in the database)
- **`Telemetry`** — `Enabled` flag and OTLP endpoint (disabled by default)
- **`WxVis`** — Conda Python path, map extent, plot retention, zoom levels (default 3)
- **`Monitor`** — Alert interval, email, severity threshold, watched services
- **`Report`** — Report interval, language, schedule, thresholds, significant-change config
- **`WxManager`** — Station lookup radius, AWC endpoint, display settings

### Recipients — database

**Recipients** (`Recipients` table): managed via WxManager → Recipients tab. `RecipientId` is the stable key; `Latitude`, `Longitude`, `MetarIcao`, `TafIcao`, and `LocalityName` are written back by `RecipientResolver` on first resolution.

**Notes on recipient fields:**
- `RecipientId` must be unique across all recipients. It is the stable key linking `Recipients` to `RecipientStates`.
- `Address` is used only for one-time geocoding; it is never displayed in reports.
- `LocalityName` is used in report subjects and body. If absent, it is inferred from geocoding on first run.
- `ScheduledSendHours` is a comma-separated string of hours (0–23) in the recipient's local timezone (e.g. `"6, 18"` for morning and evening; `"7"` for a single hour). Falls back to `DefaultScheduledSendHours` when omitted.
- `MetarIcao` accepts a comma-separated list in preference order (e.g. `"KDWH, KHOU"`). The first station with an observation within the last 3 hours is used; no DB update occurs when a fallback station is used.
- `Latitude`, `Longitude`, `MetarIcao`, `TafIcao` are written back to the database automatically by the service on first resolution. To re-trigger resolution (e.g. after a move), set them to `NULL` in the `Recipients` table.
- `TempUnit`, `PressureUnit`, `WindSpeedUnit` control how values are displayed. Each is independent. Supported values: `TempUnit`: `"F"` or `"C"`; `PressureUnit`: `"inHg"` or `"kPa"`; `WindSpeedUnit`: `"mph"` or `"kph"`. All default to US customary.

### Secrets — database

**Secrets** (`GlobalSettings` table, Id = 1): managed via WxManager → Configure tab. Stored in the database, not in configuration files. Fields: `ClaudeApiKey`, `SmtpUsername`, `SmtpPassword`, `SmtpFromAddress`. Services read these at the start of every cycle. `What3Words:ApiKey` is currently the file-based exception (in `appsettings.local.json`).

---

## 8. External Dependencies

| Service | API / Tool | Purpose | Auth |
|---|---|---|---|
| WxParser.Svc | [AWC METAR/TAF API](https://aviationweather.gov/data/api/) | Fetch weather reports | None (public) |
| WxParser.Svc / WxReport.Svc | AWC Airport API | Resolve ICAO → coordinates; nearest station lookup | None (public) |
| WxParser.Svc | [OurAirports](https://davidmegginson.github.io/ourairports-data/airports.csv) | Airport names, municipalities, coordinates for all ICAO airports | None (public) |
| WxParser.Svc | [NOAA GFS / AWS Open Data](https://noaa-gfs-bdp-pds.s3.amazonaws.com) | Download GFS GRIB2 forecast files | None (public) |
| WxParser.Svc | wgrib2.exe (native Windows) | Extract sub-grid values from GRIB2 files | n/a (local binary) |
| WxReport.Svc | [Nominatim](https://nominatim.openstreetmap.org/) | Geocode recipient address | None (User-Agent required) |
| WxReport.Svc | Anthropic Claude API | Generate natural-language reports | API key |
| WxReport.Svc / WxMonitor.Svc | Gmail SMTP | Send emails | App password |

**WxVis Python packages (conda env: wxvis, Python 3.12):**
| Package | Purpose |
|---|---|
| `metpy` | StationPlot, reduce_point_density, Barnes interpolation |
| `cartopy` | Map projections and geographic features (requires conda — C extensions) |
| `matplotlib` | Figure rendering, contour lines |
| `scipy` | Gaussian smoothing, local extrema detection |
| `sqlalchemy` + `pyodbc` | SQL Server access via Windows Authentication |
| `pandas` / `numpy` | Data manipulation and grid math |

**NuGet packages:**
| Package | Used by |
|---|---|
| `Microsoft.EntityFrameworkCore.SqlServer` | MetarParser.Data |
| `Microsoft.Extensions.Hosting.WindowsServices` | All services |
| `MailKit` | WxServices.Common |
| `log4net` | WxServices.Logging |

**System prerequisites:**
| Prerequisite | Notes |
|---|---|
| wgrib2 | NOAA native Windows build; default path `{InstallRoot}\wgrib2\wgrib2.exe` resolved via `WxPaths.Wgrib2DefaultPath`. Overrideable via `Gfs:Wgrib2Path`. |

---

## 9. Installation and Deployment

### Prerequisites
- Windows 10/11 (64-bit) — all services use `UseWindowsService`
- .NET 8 runtime
- SQL Server Express (or higher); default instance name `SQLEXPRESS`
- Gmail account with an App Password configured for SMTP
- NOAA native Windows `wgrib2.exe` installed at `{InstallRoot}\wgrib2\wgrib2.exe` (Cygwin-based build; ships with `cygwin1.dll`)
- Miniconda with the wxvis conda environment (for map rendering)
- Docker Desktop (optional, for Prometheus + Grafana observability)

### Installer

`HarderWare_WxServices.iss` is an Inno Setup script that produces a single `HarderWare_WxServices_Setup.exe` installer.  To build it:

1. Run `.\Build-Release.ps1` to publish all components into the `release\` staging directory. The script reads the product version from `Directory.Build.props` and prints the ISCC command to run.
2. Compile the `.iss` script with Inno Setup: `ISCC.exe /DAppVer=1.0.0 HarderWare_WxServices.iss` (use the version printed by the build script).

The installer copies files to the chosen directory (default `C:\HarderWare`), registers the four Windows services, updates `InstallRoot` in `appsettings.shared.json` to match the install path, creates Start Menu and optional desktop shortcuts, and launches WxManager for first-run configuration.  Uninstall stops and removes the services.

### Developer deploy script

`Deploy-WxService.ps1` (in the solution root) automates stop/publish/start for each service.  It reads `InstallRoot` from `appsettings.shared.json` and uses `$PSScriptRoot` as the solution root — no hardcoded paths to edit.  See `DEVELOPER-README.md` for full developer setup instructions.

Run from an **elevated** PowerShell prompt:

```powershell
.\Deploy-WxService.ps1 all           # Everything
.\Deploy-WxService.ps1 WxReportSvc   # Single service
.\Deploy-WxService.ps1 WxManager     # Management GUI only
.\Deploy-WxService.ps1 WxViewer      # Desktop viewer only
.\Deploy-WxService.ps1 WxVis         # Python scripts only
```

Valid names: `WxParserSvc`, `WxReportSvc`, `WxMonitorSvc`, `WxVisSvc`, `WxViewer`, `WxManager`, `WxVis`, `all`.

`all` copies WxVis Python scripts to `{InstallRoot}\WxVis\` first, then deploys the four Windows services, WxManager, and WxViewer.

### First-time install (run as Administrator)
```
sc.exe create WxParserSvc  binPath= "C:\HarderWare\BuildCache\WxServices\WxParser.Svc\bin\Release\net8.0\publish\WxParser.Svc.exe"
sc.exe create WxReportSvc  binPath= "C:\HarderWare\BuildCache\WxServices\WxReport.Svc\bin\Release\net8.0\publish\WxReport.Svc.exe"
sc.exe create WxMonitorSvc binPath= "C:\HarderWare\BuildCache\WxServices\WxMonitor.Svc\bin\Release\net8.0\publish\WxMonitor.Svc.exe"
sc.exe create WxVisSvc     binPath= "C:\HarderWare\BuildCache\WxServices\WxVis.Svc\bin\Release\net8.0\publish\WxVis.Svc.exe"

sc.exe start WxParserSvc
sc.exe start WxReportSvc
sc.exe start WxMonitorSvc
sc.exe start WxVisSvc
```

### Database setup

The database schema is managed by `DatabaseSetup.EnsureSchemaAsync()` in `MetarParser.Data`.  Every service calls this method at startup.  The first service to start creates the database (via EF Core `EnsureCreatedAsync`) and all additional tables/columns (via idempotent `IF NOT EXISTS` DDL).  Subsequent services and restarts are no-ops.  No manual SQL scripts or EF migrations are needed.

#### Startup retry against a not-yet-ready SQL Server (WX-28)

All four services start with Windows and, after a Windows-Update-driven reboot, race SQL Server's own service start.  `EnsureSchemaAsync` therefore wraps schema setup in a retry loop: each transient `SqlException` (error numbers –2, 20, 26, 40, 53, 64, 121, 233, 258, 1205, 1222, 10053, 10054, 10060, 10061, 11001) is logged at `WARN` and the service waits before the next attempt.  After `MaxAttempts` attempts have failed the method throws `DatabaseUnavailableException`; the service's outer `try/catch` logs `ERROR` and exits, leaving Windows SCM recovery actions to restart it.

Defaults: 12 attempts with delays 5 s, 10 s, 20 s, 30 s, 30 s, 30 s, 30 s, 30 s, 30 s, 30 s, 30 s (≈ 5 minutes total).  Tunable via the `Database:StartupRetry` section of `appsettings.shared.json` or `appsettings.local.json`:

```json
"Database": {
  "StartupRetry": {
    "MaxAttempts": 12,
    "DelaySecondsSchedule": [ 5, 10, 20, 30, 30, 30, 30, 30, 30, 30, 30 ]
  }
}
```

Permanent errors (login failures, permissions, schema conflicts) are *not* retried — they propagate on the first attempt so real bugs fail fast.  `EnsureCreatedAsync` — which creates the `WeatherData` database itself on first run by connecting to `master` — is inside the retry loop, so new-developer installs against a cold SQL Server still bootstrap cleanly.

The complementary pieces of WX-28 (declarative `DependOnService=MSSQL$SQLEXPRESS` in the installer, full Windows service-configuration audit, and moving `WxParser.Svc` off the personal Windows account it currently runs under) are tracked as follow-up PRs.

### Startup order
Start `WxParserSvc` first and allow at least one fetch cycle to complete before starting `WxReportSvc`, so METAR data is available for station resolution. GFS data will begin accumulating on the first 60-minute GFS cycle; full temperature forecasts appear in reports once the first complete model run is ingested (up to ~4 hours after the run's nominal time).

### Versioning

All assemblies share a single version defined in `Directory.Build.props` (`<Version>`). To bump the version, edit that one element — everything else picks it up automatically. `WxPaths.ProductVersion` reads it at runtime. `WxPaths.GitCommit` reads the short git commit hash embedded at build time via a custom `AssemblyMetadataAttribute`.

Where the version appears:
- **WxManager / WxViewer title bars** — via a `Run` element in the custom WindowChrome title bar
- **Email report footer** — `· WxServices 1.0.0` appended to the station/GFS line
- **All six program log files** — `WxReport.Svc 1.0.0 (commit abc1234) starting.`
- **Windows Apps list** — from the Inno Setup `AppVersion` (passed via `/DAppVer=` at compile time)

### Log files
All logs are written to `{InstallRoot}\Logs\` (default `C:\HarderWare\Logs\`). Log files: `wxparser-svc.log`, `wxreport-svc.log`, `wxmonitor-svc.log`, `wxvis-svc.log`, `wxmanager.log`, `wxviewer.log`, `wxvis.log` (Python). All paths are derived from the `InstallRoot` setting in `appsettings.shared.json` via the `WxPaths` class.

### Changing a recipient's location
1. In the `Recipients` database table, update `Address` and set `Latitude`, `Longitude`, `MetarIcao`, and `TafIcao` to `NULL` (or use WxManager → Recipients tab to edit the recipient).
2. The service will re-geocode and re-resolve on the next cycle.

---

## 10. Known Limitations and Future Work

| Item | Notes |
|---|---|
| Single bounding box | All METAR, TAF, and GFS data is fetched for one geographic region. Supporting recipients in widely separated locations would require per-region fetch configuration. |
| GFS requires native `wgrib2.exe` | As of WX-33, `wgrib2` runs as a native Windows process, not via WSL. If `wgrib2.exe` is missing from the configured path (default `{InstallRoot}\wgrib2\wgrib2.exe`), the GFS cycle logs errors and skips ingestion; METAR/TAF reports continue normally without forecast data. |
| GFS forecast delay | A complete model run takes up to ~4 hours after the nominal run time to appear on NOMADS. During this window the previous run's data is used. |
| WxMonitor does not watch itself | WxMonitor has no watchdog. A Windows Task Scheduler task could serve this purpose if needed. |
| Nominatim rate limit | Nominatim's terms require a maximum of 1 request/second and a valid User-Agent. Resolution is one-time per recipient, so this is unlikely to be a problem in practice. |
| WxViewer has no database access | WxViewer reads PNG files and manifest JSON files directly. METAR observation tables would require a database-connected panel in a future session. |

---

## 11. Observability

### Stack

Metrics are collected via OpenTelemetry and visualised in Grafana. The stack runs as Docker containers defined in `observability/docker-compose.yml`.

```
All services  ──OTLP/HTTP──▶  otel-collector  ──Prometheus scrape──▶  Prometheus  ──▶  Grafana
              (port 4318)      (port 8889)                              (port 9090)      (port 3000)
```

| Container | Image | Purpose |
|---|---|---|
| `otel-collector` | `otel/opentelemetry-collector:latest` | Receives OTLP metrics; exposes Prometheus scrape endpoint |
| `prometheus` | `prom/prometheus:latest` | Scrapes collector; stores time-series data |
| `grafana` | `grafana/grafana:11.4.0` | Dashboard UI (admin password: `grafana`); default timezone UTC |

Start the stack from the `observability/` directory:
```
docker compose up -d
```

### Instrumentation

All four services (WxParser.Svc, WxReport.Svc, WxVis.Svc, WxMonitor.Svc) emit metrics via `System.Diagnostics.Metrics` and export over OTLP/HTTP every 10 seconds. Each service registers its own `Meter` and instruments; see the metrics tables in each service's section above.

Telemetry is disabled by default. To enable it, set `Telemetry:Enabled` to `true` in `appsettings.shared.json`. The OTLP endpoint is configured by `Telemetry:OtlpEndpoint` (default `http://localhost:4318/v1/metrics`). When disabled, no exporter is registered and no background HTTP traffic occurs. **The full signal path (`/v1/metrics`) must be included — the SDK does not append it automatically when the endpoint is set in code.**

### Grafana dashboards

Four provisioned dashboards in `observability/grafana/dashboards/`, auto-loaded by Grafana:

| Dashboard | File | UID | Key panels |
|---|---|---|---|
| WxParser | `wxparser.json` | `wxparser-v1` | Fetch cycles, cycle duration p50/p95 |
| WxReport | `wxreport.json` | `wxreport-v1` | Cycle count, sends, failures, Claude calls/duration |
| WxVis | `wxvis.json` | `wxvis-v1` | Analysis/forecast render counts, failures, render duration |
| WxMonitor | `wxmonitor.json` | `wxmonitor-v1` | Monitor cycles, alerts sent, alert activity |

All dashboards are tagged `wxservices` for easy filtering.

### Useful Prometheus queries

| Query | What it shows |
|---|---|
| `wxparser_fetch_cycles_total` | Cumulative completed METAR/TAF fetch cycles |
| `increase(wxreport_sends_total[1h])` | Reports sent in the last hour |
| `wxvis_forecast_renders_total` | Cumulative forecast frames rendered |
| `increase(wxmonitor_alerts_total[1h])` | Monitor alerts sent in the last hour |
| `histogram_quantile(0.95, sum(increase(wxreport_claude_duration_seconds_bucket[30m])) by (le))` | p95 Claude API call duration |

Use `increase()` rather than `rate()` for these metrics — service cycles fire infrequently, so `rate()` produces near-zero values that cause `histogram_quantile` to return NaN.
