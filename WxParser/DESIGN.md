# WxParser System — Design Document

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
   - [WxMonitor.Svc — Health Monitor](#43-wxmonitorsvc--health-monitor)
5. [Class Libraries](#5-class-libraries)
6. [Data Model](#6-data-model)
7. [Configuration Guide](#7-configuration-guide)
8. [External Dependencies](#8-external-dependencies)
9. [Installation and Deployment](#9-installation-and-deployment)
10. [Known Limitations and Future Work](#10-known-limitations-and-future-work)

---

## 1. Purpose

WxParser is a set of Windows services that:

- Periodically fetch METAR and TAF aviation weather reports from the Aviation Weather Center API and store them in a local SQL Server database.
- Generate friendly, plain-English (or other language) weather summaries using Anthropic's Claude AI and email them to a configured list of recipients.
- Monitor the health of the above services and send alert emails if errors occur or a service goes silent.

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

    PARSER["WxParser.Svc (every 10 min)"]
    REPORT["WxReport.Svc (every 5 min)"]
    MONITOR["WxMonitor.Svc (every 5 min)"]

    AWC -->|weather data| PARSER
    PARSER -->|store observations| DB
    PARSER -->|log + heartbeat| LOGS

    DB -->|latest conditions| REPORT
    CLAUDE -->|report text| REPORT
    REPORT -->|email| RECIPIENTS
    REPORT -->|log + heartbeat| LOGS

    LOGS -->|scan| MONITOR
    MONITOR -->|alert email| ALERTS
```

---

### 2.2 WxParser.Svc — data flow

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
    LOOKUP --> SAVE["Cache to appsettings.local.json"]
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

### 2.3 WxReport.Svc — data flow

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
    NEAREST --> CACHE["Cache to appsettings.local.json"]
    CACHE --> SNAP

    RESOLVED -->|Yes| SNAP["Build WeatherSnapshot from DB"]
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

### 2.4 WxMonitor.Svc — data flow

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
WxParser/
├── DESIGN.md                        ← this file
├── WxParser.sln
├── appsettings.shared.json          ← shared fetch-region config (git-tracked)
└── src/
    ├── MetarParser/                 ← METAR text parser library
    ├── TafParser/                   ← TAF text parser library
    ├── MetarParser.Data/            ← EF Core entities, fetchers, DB context, geocoders
    ├── WxParser.Logging/            ← log4net wrapper (static Logger class)
    ├── WxInterp/                    ← snapshot interpreter (METAR+TAF → WeatherSnapshot)
    ├── WxParser.Svc/                ← Windows service: periodic fetch
    ├── WxReport.Svc/                ← Windows service: report generation and email
    └── WxMonitor.Svc/               ← Windows service: log and heartbeat monitoring
tests/
    ├── MetarParser.Tests/
    ├── TafParser.Tests/
    └── WxInterp.Tests/
```

### Project dependency graph

```mermaid
graph TD
    LOG[WxParser.Logging]
    MDATA[MetarParser.Data]
    MP[MetarParser]
    TP[TafParser]
    INTERP[WxInterp]
    PSVC[WxParser.Svc]
    RSVC[WxReport.Svc]
    MSVC[WxMonitor.Svc]

    MDATA --> MP
    MDATA --> TP
    INTERP --> MDATA
    PSVC --> MDATA
    PSVC --> LOG
    RSVC --> INTERP
    RSVC --> MDATA
    RSVC --> LOG
    MSVC --> LOG
```

---

## 4. Service Details

### 4.1 WxParser.Svc — Data Fetcher

**Purpose:** Keep the local database populated with current METAR and TAF data.

**Cycle (default: every 10 minutes):**
1. Resolve home coordinates from `appsettings.shared.json` (`HomeLatitude`, `HomeLongitude`). If absent, look up via `AirportLocator` using `HomeIcao` and cache to `appsettings.local.json`.
2. Fetch all METARs within a configurable bounding box (default ±5°) around home coordinates via the AWC API.
3. Fetch the home ICAO station explicitly (in case it falls outside the bounding box result).
4. Fetch all TAFs within the same bounding box.
5. Insert new records; skip duplicates (unique index on station + observation time + report type).
6. Write the current UTC timestamp to `wxparser-heartbeat.txt`.

**Key classes:**
| Class | Location | Role |
|---|---|---|
| `FetchWorker` | WxParser.Svc | `BackgroundService`; owns the fetch loop |
| `MetarFetcher` | MetarParser.Data | AWC API call → parse → insert METARs |
| `TafFetcher` | MetarParser.Data | AWC API call → parse → insert TAFs |
| `MetarParser` | MetarParser | Parses raw METAR text into structured objects |
| `TafParser` | TafParser | Parses raw TAF text into structured objects |
| `AirportLocator` | MetarParser.Data | AWC API: resolves ICAO to lat/lon |

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
    D -->|No| E[Geocode address\nFind nearest METAR + TAF\nCache to appsettings.local.json]
    E --> F{Success?}
    F -->|No| SKIP[Skip recipient]
    D -->|Yes| G[Build WeatherSnapshot]
    F -->|Yes| G
    G --> H[Compute fingerprint]
    H --> I{Should send?}
    I -->|No| C
    I -->|Yes — scheduled / first / change| J[SnapshotDescriber → structured text]
    J --> K[Claude API → report text]
    K --> L[Send email]
    L --> M[Update RecipientState in DB]
    M --> C
```

**Send-decision logic:**
- **First run:** Immediately sends a welcome + weather report.
- **Scheduled:** Sends once per day when the recipient's configured local hour arrives (default 07:00).
- **Significant change:** Sends an unscheduled report when the weather fingerprint changes, subject to a minimum gap between sends (default 60 minutes).

**Significant-change thresholds (configurable):**
| Condition | Default threshold |
|---|---|
| Wind speed | ≥ 25 kt |
| Visibility | < 3.0 SM |
| Ceiling | < 3,000 ft AGL |

**METAR station fallback (tiered):**
1. Try each ICAO in the recipient's `MetarIcao` list (comma-separated, preference order).
2. Fall back to any station in the database with data in the last 3 hours.
3. If no data at all, skip the recipient for this cycle.

The config is never updated when a fallback station is used; a warning is logged.

**Recipient resolution (one-time, cached):**
1. Geocode `Address` via Nominatim → lat/lon + locality name.
2. Query the database for all recently-reporting METAR stations; resolve each to coordinates via AirportLocator; pick the nearest.
3. Same for TAF stations.
4. Write lat, lon, `MetarIcao`, `TafIcao`, and `LocalityName` back to `appsettings.local.json`.

**Key classes:**
| Class | Role |
|---|---|
| `ReportWorker` | `BackgroundService`; owns the report loop |
| `RecipientResolver` | Address geocoding and station resolution; cache write-back |
| `WxInterpreter` | Queries DB → `WeatherSnapshot`; station fallback logic |
| `SnapshotDescriber` | `WeatherSnapshot` → structured plain-text for Claude |
| `ClaudeClient` | Anthropic Messages API wrapper |
| `EmailSender` | MailKit SMTP wrapper |
| `SnapshotFingerprint` | Computes a change-detection hash from significant weather fields |

---

### 4.3 WxMonitor.Svc — Health Monitor

**Purpose:** Alert the operator by email when either watched service logs errors or goes silent.

**Cycle (default: every 5 minutes):**
1. For each watched service, scan its log file for entries at or above `AlertOnSeverity` (default: ERROR) with a timestamp newer than the last one processed.
2. For each watched service, read its heartbeat file and compare its age to `HeartbeatMaxAgeMinutes`.
3. Send an alert email if issues are found and the per-service, per-alert-type cooldown (`AlertCooldownMinutes`, default 60) has elapsed.
4. Persist state (last-seen log timestamp, last-alert timestamps) to `wxmonitor-state.json`.

**First-run behaviour:** On first run, `LastSeenLogTimestamp` is null. The scanner baselines to the latest entry in the log without sending alerts, so installation does not flood the inbox with historical errors.

**Key classes:**
| Class | Role |
|---|---|
| `MonitorWorker` | `BackgroundService`; owns the monitor loop |
| `LogScanner` | Parses log file; handles multi-line entries (stack traces); filters by severity and timestamp |
| `HeartbeatChecker` | Reads heartbeat file; returns age |
| `AlertEmailSender` | MailKit SMTP wrapper |
| `MonitorStateStore` | Reads/writes `wxmonitor-state.json` |

---

## 5. Class Libraries

### WxParser.Logging

A thin static wrapper around log4net. All services call `Logger.Initialise()` once at startup; thereafter `Logger.Info/Warn/Error/Fatal` are available everywhere. Caller file, method, and line number are captured automatically via `[CallerFilePath]` etc.

Log format: `yyyy-MM-dd HH:mm:ss.fff LEVEL [File::Method:Line] message`

### WxInterp

Translates raw database entities into a language-neutral `WeatherSnapshot` value object, and exposes static helpers to find the nearest METAR/TAF station in the database to a given coordinate.

Key types:
- `WeatherSnapshot` — current conditions + TAF forecast periods
- `WxInterpreter` — queries DB, applies unit conversions, builds snapshot
- `ForecastPeriod`, `SkyLayer`, `SnapshotWeather` — snapshot sub-types

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

    RecipientStates {
        int Id PK
        string RecipientId
        datetime LastScheduledSentUtc
        datetime LastUnscheduledSentUtc
        string LastSnapshotFingerprint
    }

    Metars ||--o{ MetarSkyConditions : "has"
    Metars ||--o{ MetarWeatherPhenomena : "has"
    Metars ||--o{ MetarRunwayVisualRanges : "has"
    Tafs ||--o{ TafChangePeriods : "has"
    TafChangePeriods ||--o{ TafChangePeriodSkyConditions : "has"
    TafChangePeriods ||--o{ TafChangePeriodWeatherPhenomena : "has"
```

### Key indexes

| Table | Index | Type |
|---|---|---|
| Metars | StationIcao + ObservationUtc + ReportType | Unique |
| Metars | StationIcao | Non-unique |
| Tafs | StationIcao + IssuanceUtc + ReportType | Unique |
| Tafs | StationIcao | Non-unique |
| RecipientStates | RecipientId | Unique |

---

## 7. Configuration Guide

### File layering

Each service loads configuration from up to three files, merged in order (later files win):

| File | Tracked by git | Purpose |
|---|---|---|
| `appsettings.shared.json` | Yes | Fetch-region settings shared by all services |
| `appsettings.json` | Yes | Service-specific non-secret settings |
| `appsettings.local.json` | **No** | Secrets, per-recipient data, and cached resolved values |

### appsettings.shared.json (WxParser root)

```json
{
  "Fetch": {
    "HomeIcao": "KDWH",
    "HomeLatitude": 30.0,
    "HomeLongitude": -95.5,
    "BoundingBoxDegrees": 5.0
  }
}
```

### WxParser.Svc — appsettings.json

```json
{
  "Fetch": {
    "IntervalMinutes": 10,
    "HeartbeatFile": "C:\\HarderWare\\Logs\\wxparser-heartbeat.txt"
  }
}
```

### WxReport.Svc — appsettings.json

```json
{
  "Report": {
    "IntervalMinutes": 5,
    "HeartbeatFile": "C:\\HarderWare\\Logs\\wxreport-heartbeat.txt",
    "DefaultLanguage": "English",
    "DefaultScheduledSendHour": 7,
    "MinGapMinutes": 60,
    "SignificantChange": {
      "WindThresholdKt": 25,
      "VisibilityThresholdSm": 3.0,
      "CeilingThresholdFt": 3000
    },
    "Claude": {
      "Model": "claude-haiku-4-5-20251001"
    },
    "Smtp": {
      "Host": "smtp.gmail.com",
      "Port": 587,
      "FromName": "WxReport"
    }
  }
}
```

### WxReport.Svc — appsettings.local.json

```json
{
  "ConnectionStrings": {
    "WeatherData": "Server=.\\SQLEXPRESS;Database=WeatherData;Trusted_Connection=True;..."
  },
  "Report": {
    "Claude": { "ApiKey": "sk-ant-..." },
    "Smtp": {
      "Username": "you@gmail.com",
      "Password": "your-app-password",
      "FromAddress": "you@gmail.com"
    },
    "Recipients": [
      {
        "Id": "paul-en",
        "Email": "you@gmail.com",
        "Name": "Paul",
        "Language": "English",
        "Timezone": "America/Chicago",
        "ScheduledSendHour": 7,
        "Address": "123 Main St, The Woodlands TX 77380",
        "LocalityName": "The Woodlands",
        "Latitude": 30.1658,
        "Longitude": -95.4613,
        "MetarIcao": "KDWH, KHOU",
        "TafIcao": "KIAH"
      }
    ]
  }
}
```

**Notes on recipient config:**
- `Id` must be unique across all recipients. It is used as the stable key in `RecipientStates`.
- `Address` is used only for one-time geocoding; it is never displayed in reports.
- `LocalityName` is used in report subjects and body. If omitted, it is inferred from geocoding.
- `MetarIcao` accepts a comma-separated list in preference order (e.g. `"KDWH, KHOU"`). The first station with data is used; no config update occurs when a fallback station is used.
- `Latitude`, `Longitude`, `MetarIcao`, `TafIcao` are written back automatically by the service on first run. To trigger re-resolution (e.g. after a move), set them to `null`.

### WxMonitor.Svc — appsettings.json

```json
{
  "Monitor": {
    "IntervalMinutes": 5,
    "AlertEmail": "PaulHarder2@gmail.com",
    "AlertOnSeverity": "ERROR",
    "AlertCooldownMinutes": 60,
    "WatchedServices": [
      {
        "Name": "WxParser.Svc",
        "LogFile": "C:\\HarderWare\\Logs\\wxparser-svc.log",
        "HeartbeatFile": "C:\\HarderWare\\Logs\\wxparser-heartbeat.txt",
        "HeartbeatMaxAgeMinutes": 90
      },
      {
        "Name": "WxReport.Svc",
        "LogFile": "C:\\HarderWare\\Logs\\wxreport-svc.log",
        "HeartbeatFile": "C:\\HarderWare\\Logs\\wxreport-heartbeat.txt",
        "HeartbeatMaxAgeMinutes": 15
      }
    ],
    "Smtp": {
      "Host": "smtp.gmail.com",
      "Port": 587,
      "FromName": "WxMonitor"
    }
  }
}
```

### WxMonitor.Svc — appsettings.local.json

```json
{
  "Monitor": {
    "Smtp": {
      "Username": "you@gmail.com",
      "Password": "your-app-password",
      "FromAddress": "you@gmail.com"
    }
  }
}
```

---

## 8. External Dependencies

| Service | API | Purpose | Auth |
|---|---|---|---|
| WxParser.Svc | [AWC METAR/TAF API](https://aviationweather.gov/data/api/) | Fetch weather reports | None (public) |
| WxParser.Svc / WxReport.Svc | AWC Airport API | Resolve ICAO → coordinates | None (public) |
| WxReport.Svc | [Nominatim](https://nominatim.openstreetmap.org/) | Geocode recipient address | None (User-Agent required) |
| WxReport.Svc | Anthropic Claude API | Generate natural-language reports | API key |
| WxReport.Svc / WxMonitor.Svc | Gmail SMTP | Send emails | App password |

**NuGet packages:**
| Package | Used by |
|---|---|
| `Microsoft.EntityFrameworkCore.SqlServer` | MetarParser.Data |
| `Microsoft.Extensions.Hosting.WindowsServices` | All services |
| `MailKit` | WxReport.Svc, WxMonitor.Svc |
| `log4net` | WxParser.Logging |

---

## 9. Installation and Deployment

### Prerequisites
- Windows (all three projects use `UseWindowsService`)
- .NET 8 runtime
- SQL Server (Express is sufficient); instance name `SQLEXPRESS` by default
- Gmail account with an App Password configured for SMTP

### Build
```
dotnet publish src\WxParser.Svc\WxParser.Svc.csproj -c Release -o C:\HarderWare\WxParser.Svc
dotnet publish src\WxReport.Svc\WxReport.Svc.csproj -c Release -o C:\HarderWare\WxReport.Svc
dotnet publish src\WxMonitor.Svc\WxMonitor.Svc.csproj -c Release -o C:\HarderWare\WxMonitor.Svc
```

### Install services (run as Administrator)
```
sc.exe create WxParserSvc  binPath= "C:\HarderWare\WxParser.Svc\WxParser.Svc.exe"
sc.exe create WxReportSvc  binPath= "C:\HarderWare\WxReport.Svc\WxReport.Svc.exe"
sc.exe create WxMonitorSvc binPath= "C:\HarderWare\WxMonitor.Svc\WxMonitor.Svc.exe"

sc.exe start WxParserSvc
sc.exe start WxReportSvc
sc.exe start WxMonitorSvc
```

### Startup order
Start `WxParserSvc` first and allow at least one fetch cycle to complete before starting `WxReportSvc`, so METAR data is available for station resolution.

### Log files
All logs are written to `C:\HarderWare\Logs\`. Log paths can be changed by editing `log4net.config` in each service's output directory.

### Updating a service
1. Stop the service: `sc.exe stop WxReportSvc`
2. Publish the new build over the existing directory.
3. Start the service: `sc.exe start WxReportSvc`

### Changing a recipient's location
1. In `appsettings.local.json`, update `Address` and set `Latitude`, `Longitude`, `MetarIcao`, and `TafIcao` to `null`.
2. The service will re-geocode and re-resolve on the next cycle.

---

## 10. Known Limitations and Future Work

| Item | Notes |
|---|---|
| No temperature forecast | TAF reports do not include temperature data. Forecast temperature would require GRIB data, which is a significantly larger undertaking. |
| Single bounding box | All data is fetched for one geographic region. Supporting recipients in widely separated locations would require per-region fetch configuration. |
| No metrics | Cycle duration, API call counts, send success rates, etc. are not tracked. Consider adding structured metrics (Seq, Windows Event Log) in a future session. |
| WxMonitor does not watch itself | WxMonitor has no watchdog. A Windows Task Scheduler task could serve this purpose if needed. |
| Nominatim rate limit | Nominatim's terms require a maximum of 1 request/second and a valid User-Agent. Resolution is one-time per recipient, so this is unlikely to be a problem in practice. |
