# Version History

This project follows [Semantic Versioning](https://semver.org/): **MAJOR.MINOR.PATCH**.
Patch releases are bug fixes, minor releases introduce new features, and major releases are reserved for large changes.

| Version | Commit  | Date       | Summary |
|---------|---------|------------|---------|
| 1.3.0   | d70f708 | 2026-04-15 | WxStations country/region columns (WX-13) |
| 1.2.1   | 83b9a29 | 2026-04-14 | WxViewer zoom-swap fix; numpad zoom controls |
| 1.2.0   | 211282c | 2026-04-13 | Metrics instrumentation across all services; report footer branding |
| 1.1.1   | a0a3b5c | 2026-04-13 | Bug fixes, logging audit, meteogram cosmetic fix |
| 1.1.0   | f6fd52c | 2026-04-12 | Multi-zoom map rendering with interactive zoom/pan |
| 1.0.2   | d55384e | 2026-04-12 | Meteogram temp-unit bug fix; log format cleanup |
| 1.0.1   | ef49de4 | 2026-04-11 | Version display and startup logging |
| 1.0.0   | 7a2a268 | 2026-04-07 | Initial versioned release |

---

## 1.3.0 — WxStations country/region columns (2026-04-15)

- **Six new nullable columns on `WxStations`** (WX-13): `Region`, `RegionCode`, `RegionAbbr`, `Country`, `CountryCode`, `CountryAbbr`. Populated from OurAirports' `airports.csv`, `countries.csv`, and `regions.csv`. `CountryAbbr` uses a small override table (currently `GB`→`UK`, `US`→`USA`); all other countries default to their ISO 3166-1 alpha-2 code.
- **Schema migration is idempotent.** `DatabaseSetup.EnsureSchemaAsync` appends six `ALTER TABLE ... IF NOT EXISTS` blocks; fresh installs pick up the columns from `OnModelCreating`.
- **WxManager Nearby Stations grid** now displays `"{Municipality}, {RegionAbbr}, {CountryAbbr}"` (e.g. `Brenham, TX, USA`), falling back to the airport name when location parts are unavailable.
- **Importer enhancements.** `AirportDataImporter` now downloads `countries.csv` and `regions.csv` alongside `airports.csv`, builds in-memory lookups, splits `iso_region` at the hyphen for the short region abbreviation, and upserts all six fields on every refresh cycle — providing automatic backfill of existing rows on the next run.
- **Expected coverage.** Of ~39 300 stations imported, only the ~45 stub rows inserted by `MetarFetcher` for ICAOs not present in OurAirports remain with NULL location fields; the station-grid display handles those gracefully.

## 1.2.1 — WxViewer zoom-swap fix; numpad zoom controls (2026-04-14)

- **Zoom-level transitions no longer jump the view.** The Z1↔Z2 and Z2↔Z3 swaps previously multiplied the WPF scale and translate by 0.5/2.0 to compensate for what was assumed to be a 2x-sized bitmap. Because the `Image` controls use `Stretch="Uniform"`, a bitmap of any Z-level fills the same viewport at `ScaleX=1`, so the compensation was *introducing* the jump rather than undoing one. The swap now leaves scale and translate untouched — the point under the cursor stays put and magnification is monotonic across transitions.
- **Level-dependent swap thresholds.** Each Z-level has 2x the pixel density of the one below it, so the "stretched enough to look blurry" scale doubles per level: Z1→Z2 at `ScaleX ≥ 3.0`, Z2→Z3 at `≥ 6.0`; swap-down coincides with the previous level's up threshold.
- **Direction-aware swap checks.** A zoom-in step can only trigger a swap-up, a zoom-out step only a swap-down. Up and down thresholds can therefore coincide without ping-pong, and the same keypress that triggered a swap-up also reverses it.
- **Crossfade tuning.** Duration raised from 200 ms to 1000 ms with starting opacity dropped to 0.1, so the transition reads as a fade rather than a flash.
- **Numpad zoom controls.** NumPad 8 zooms in one step (×1.15, matching one mouse-wheel click), NumPad 2 zooms out one step, anchored on the cursor when it's over a viewport and mirrored to the linked pane when Link Panes is enabled. Implemented via a `WM_KEYDOWN` WndProc hook that inspects the scan-code extended-key bit in `lParam`, so the numpad keys work regardless of NumLock state and do not hijack the dedicated arrow-block Up/Down keys.

## 1.2.0 — Metrics instrumentation across all services (2026-04-13)

- **OpenTelemetry metrics added to all four services**, exporting via OTLP to the existing Prometheus/Grafana observability stack. All services share the `Telemetry:Enabled` and `Telemetry:OtlpEndpoint` settings in `appsettings.shared.json`.
- **WxParser.Svc (GFS):** Added `wxparser.gfs.cycles.total`, `wxparser.gfs.failures.total`, and `wxparser.gfs.cycle.duration.seconds` (histogram, buckets 30s–30min) to the existing METAR/TAF metrics.
- **WxReport.Svc:** New meter with `wxreport.cycles.total`, `wxreport.sends.total`, `wxreport.send.failures.total`, `wxreport.claude.calls.total`, `wxreport.cycle.duration.seconds`, and `wxreport.claude.duration.seconds`.
- **WxVis.Svc:** New meter with `wxvis.analysis.renders.total`, `wxvis.analysis.failures.total`, `wxvis.forecast.renders.total`, `wxvis.forecast.failures.total`, and `wxvis.render.duration.seconds`.
- **WxMonitor.Svc:** New meter with `wxmonitor.cycles.total` and `wxmonitor.alerts.total`.
- **Report footer branding:** Email footer now reads "HarderWare WxServices 1.2.0" instead of "WxServices 1.2.0".

## 1.1.1 — Bug fixes, logging audit, meteogram cosmetic fix (2026-04-13)

- **Bug fix:** ComboBoxes (Analysis selector, GFS Run selector, speed selectors, meteogram run/recipient selectors) retained keyboard focus after selection, trapping arrow keys. All six ComboBoxes now return focus to the main window via `DropDownClosed` + `Keyboard.Focus(this)`.
- **WxViewer logging and error-handling audit:**
  - Global exception handlers: `DispatcherUnhandledException`, `AppDomain.UnhandledException`, and `TaskScheduler.UnobservedTaskException` registered at startup so unhandled exceptions are logged before the process terminates.
  - All previously-silent catch blocks now log via `Logger.Warn`: settings parsing, database queries, image loading, manifest parsing, and `FileSystemWatcher` errors.
  - `MainWindow.OnClosed` unsubscribes ViewModel event handlers and stops the quality-restoration timer.
  - `MainViewModel.Dispose` unsubscribes `MapFileScanner.DirectoryChanged` (converted from lambda to named method).
  - Highlight timer reused as a single instance with a named tick handler instead of being recreated on each highlight call.
- **Meteogram cosmetic fix:** Right-axis tick labels now rendered in green to match the RH line and axis label.

## 1.1.0 — Multi-zoom map rendering with interactive zoom/pan (2026-04-12)

- **Multi-zoom rendering pipeline:** `synoptic_map.py` and `forecast_map.py` accept `--zoom-level N`. Each level doubles figure size and halves station density. Font sizes and contour line widths scale proportionally. DPI is 150 for z1, 100 for z2+ to manage file sizes.
- C# workers (`AnalysisMapWorker`, `ForecastMapWorker`) loop over configurable zoom levels (default 3) per render cycle. New `WxVisConfig.ZoomLevels` property. Filenames include `_z{N}` suffix. `MapFileScanner` updated with new regex patterns and `ZoomPaths` dictionary.
- **WxViewer zoom/pan UX:** Mouse-wheel zoom toward cursor, click-drag pan, double-click to reset. Zoom-in at 3.0× crossfades to the next higher-resolution image; zoom-out at 1.4× swaps back. Link Panes toggle synchronizes zoom/pan between both panes. Reset Zoom button and zoom-level indicator (Z1/Z2/Z3) in toolbars.
- CONUS map extent expanded from (-126, -65, 22, 50) to (-136, -60, 17, 55) to prevent Lambert Conformal clipping at edges
- Low-quality bitmap scaling during active interaction for responsiveness, restored to high-quality after 200 ms idle
- Maximized window constrained to working area to avoid taskbar overlap

## 1.0.2 — Meteogram temp-unit bug fix; log format cleanup (2026-04-12)

- **Bug fix:** Recipients configured for Celsius were receiving Fahrenheit meteograms because (1) PNG filenames did not include the temperature unit, causing C and F renders to overwrite each other, and (2) `FindMeteogramAbbrevPath` matched only on ICAO + timezone, ignoring `TempUnit`. Fixed by adding a `{F|C}` tag to filenames and a `tempUnit` parameter to the finder.
- Standardized all ReportWorker recipient log lines to the format `{Id} {Email} ({Name})`
- GFS forecast summary log now respects the recipient's temperature unit preference

## 1.0.1 — Version display and startup logging (2026-04-11)

- Version number shown in WxManager and WxViewer custom title bars (styled smaller and dimmer)
- Version and short git commit hash logged at startup for all six programs (e.g. `WxReport.Svc 1.0.1 (commit ef49de4) starting.`)
- Suppressed the SDK `+hash` suffix from `InformationalVersion` so title bars and email footers display a clean version string
- Added Versioning section to DESIGN.md

## 1.0.0 — Initial versioned release (2026-04-07)

- Introduced system-wide version number via `Directory.Build.props`, replacing per-project versioning
- All six programs (four services, WxManager, WxViewer) share a single version source of truth
- Git commit hash embedded as `AssemblyMetadata("GitCommit", ...)` for runtime diagnostics
