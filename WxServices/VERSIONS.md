# Version History

This project follows [Semantic Versioning](https://semver.org/): **MAJOR.MINOR.PATCH**.
Patch releases are bug fixes, minor releases introduce new features, and major releases are reserved for large changes.

| Version | Commit  | Date       | Summary |
|---------|---------|------------|---------|
| 1.3.8   | 79ec475 | 2026-04-23 | Promote TabControl.SelectionChanged diagnostic to permanent logging (WX-48) |
| 1.3.7   | 726c01d | 2026-04-18 | Analysis map MSLP now uses proper temperature-based reduction from altimeter (WX-35) |
| 1.3.6   | fa6c0c3 | 2026-04-18 | GFS pipeline switched from WSL-invoked wgrib2 to native Windows wgrib2.exe (WX-33) |
| 1.3.5   | 115d037 | 2026-04-17 | Retry DB connection at service startup with per-attempt WARN + final ERROR (WX-28, partial) |
| 1.3.4   | d3996df | 2026-04-16 | Retry with exponential backoff on transient upstream fetch failures (WX-20, WX-21) |
| 1.3.3   | 5205064 | 2026-04-16 | Defensive idempotent ALTER for Metars/Tafs ReceivedUtc (WX-22) |
| 1.3.2   | 5569f78 | 2026-04-16 | Geographic nearest-neighbour METAR fallback within 30 mi (WX-19) |
| 1.3.1   | d9ec9fc | 2026-04-15 | Forecast temperatures formatted on two labeled lines (WX-14) |
| 1.3.0   | d70f708 | 2026-04-15 | WxStations country/region columns (WX-13) |
| 1.2.1   | 83b9a29 | 2026-04-14 | WxViewer zoom-swap fix; numpad zoom controls |
| 1.2.0   | 211282c | 2026-04-13 | Metrics instrumentation across all services; report footer branding |
| 1.1.1   | a0a3b5c | 2026-04-13 | Bug fixes, logging audit, meteogram cosmetic fix |
| 1.1.0   | f6fd52c | 2026-04-12 | Multi-zoom map rendering with interactive zoom/pan |
| 1.0.2   | d55384e | 2026-04-12 | Meteogram temp-unit bug fix; log format cleanup |
| 1.0.1   | ef49de4 | 2026-04-11 | Version display and startup logging |
| 1.0.0   | 7a2a268 | 2026-04-07 | Initial versioned release |

---

## 1.3.8 — TabControl.SelectionChanged diagnostic promoted to permanent (2026-04-23)

- **WX-48 (instrumentation):** The diagnostic `SelectionChanged` handler developed on `WX-46-maps-tab-jump` (commit `59b2d9c`, originally marked *"branch-only, no PR"*) is now part of master. Feature branches forked from master will inherit the instrumentation automatically, and any WX-46 recurrence in any installed build will leave a forensic record in the log.
- **What's captured.** On each genuine `TabControl` selection change (the `OriginalSource` guard filters bubbled events from nested ComboBoxes and similar), the handler emits two log lines: a compact one-liner with the added/removed `TabItem` and its `Header`, the current `Keyboard.FocusedElement` type and `x:Name`, and both `e.OriginalSource` and `e.Source` types; and a full managed `StackTrace(fNeedFileInfo: true)` so the caller into `Selector.OnSelectionChanged` is captured. Frames of interest include `TabItem.OnGotKeyboardFocusWithin` (focus-traversal theory), `TabItem.OnRequestBringIntoView` (a Meteograms descendant asked to scroll into view), any of our own code (programmatic `SelectedIndex` set), or pure WPF internals with no obvious trigger (dispatcher-queued operation).
- **Why ship to master now.** WX-46 is an intermittent bug. Keeping the diagnostic confined to the `WX-46-maps-tab-jump` branch meant any WxViewer build derived from master would not log recurrences — exactly the scenario where recurrence data is most likely to appear (sustained daily use of the installed product). Shipping the diagnostic to master closes that gap.
- **No user-visible change.** Output is log-only; no UI, data, or behaviour shift.
- **WX-46 ticket.** Remains open, awaiting the next recurrence — the installed build now has the diagnostic wired in, so the next trigger will produce a stack trace for root-cause analysis.

## 1.3.7 — Analysis-map MSLP: proper temperature-based reduction (2026-04-18)

- **WX-35 (bug fix):** `WxVis/synoptic_map.py::_altimeter_to_slp_hpa` was adding `h_meters / 8.5` hPa on top of the METAR altimeter setting. That barometric approximation is the formula for reducing **station pressure** to MSL — but the altimeter setting (QNH) is *already* reduced to MSL using the ISA temperature profile. The extra reduction inflated the plotted "SLP" in direct proportion to elevation: at KDEN (5280 ft) the function returned ≈1205 hPa for a 30.00 inHg altimeter; at KLXV (9927 ft) it returned ≈1372 hPa. Barnes interpolation then built a fake 300 hPa dome over the Rockies and 4 hPa isobar contours proliferated across the middle of the map, producing the visibly denser isobar field than the companion GFS f000 map for the same valid time.
- **Fix:** new `_altimeter_to_mslp_hpa(altimeter_value, altimeter_unit, elevation_ft, temp_c)` performs the physically correct two-step reduction — QNH → station pressure via the ISA polytropic formula `P_stn = QNH × (1 - 0.0065·h/288.15)^5.2561`, then station pressure → MSLP via the hypsometric equation `MSLP = P_stn × exp(g·h / (R_d · T_mean))` with `T_mean = T_surface_K + 0.00325·h` (6.5 K/km standard lapse for the fictitious surface-to-MSL layer). The `AirTemperatureCelsius` column is already materialised on the plot DataFrame at `prepare_plot_data`, so the call site just threads it through.
- **Degenerate cases.** Station at or below MSL (including missing/non-finite `ElevationFt`), or missing/non-finite `AirTemperatureCelsius`, fall back to returning QNH directly — the error vs. true MSLP is <1 hPa at low elevations and the analysis remains self-consistent across stations.
- **Verified end-to-end.** KCXO (245 ft, 29.88 inHg, 20°C): 1011.7 hPa (was 1020.6). KDEN (5280 ft, 30.00 inHg, 10°C): 1012.2 hPa (was 1205.3). KLXV (9927 ft, 30.00 inHg, 0°C): 1009.5 hPa (was 1371.9). The Rockies no longer anchor an artificial high; isobar density on the 06Z analysis map now matches the companion GFS f000 map.
- **Non-goals.** The 3-digit station-model encoding `(slp_hpa * 10) % 1000` at `_encode_slp` is untouched (it is standard WMO format and was always correct — it just inherited wrong inputs). Forecast-side MSLP handling in `forecast_map.py` reads MSLP in Pa from GRIB and was never affected.

## 1.3.6 — GFS pipeline switched from WSL to native Windows wgrib2 (2026-04-18)

- **WX-33 (bug fix):** 1.3.5 introduced a regression when WX-31 Phase 1 moved `WxParser.Svc` off `.\PaulH` onto the `NT SERVICE\WxParserSvc` virtual account. `GribExtractor` invoked `wgrib2` via `wsl.exe` — but WSL distros are per-Windows-user, and virtual service accounts have no WSL of their own. Every `wgrib2 -small_grib` call exited `-1`, every forecast hour produced zero subgrid data, and the 2026-04-17 18Z GFS run was lost entirely (`GfsFetcher: run 2026-04-17 18Z is 0/121 hours complete`). `ForecastMapWorker` correctly idled for lack of new data, so forecast-map output stopped after the 12Z run rendered at 19:10 UTC.
- **Fix:** switched to the NOAA native Windows build of `wgrib2` (Cygwin-compiled, ships `wgrib2.exe` + `cygwin1.dll`). `GribExtractor.ExtractAsync` now spawns `wgrib2.exe` directly with Windows paths — no `wsl.exe` wrapper, no `/mnt/c/...` path translation. Works under any identity because the Windows exe has no per-user prerequisites.
- **Config key renamed:** `Gfs:Wgrib2WslPath` → `Gfs:Wgrib2Path`. Clean rename with no back-compat shim — the old key in an existing `appsettings.local.json` will be ignored and the default from `WxPaths.Wgrib2DefaultPath` (`{InstallRoot}\wgrib2\wgrib2.exe`) will apply. Operators updating the new key in WxManager's Configure tab will write the new key name going forward.
- **`WxPaths.Wgrib2BundledWslPath` → `Wgrib2DefaultPath`.** Default resolves to `{InstallRoot}\wgrib2\wgrib2.exe` (previously a WSL path under `{InstallRoot}/tools/`). The `ToolsDir` property and the internal `ToWslPath` helper were retired along with the WSL code path.
- **WSL retired as a prerequisite.** It was only there to host `wgrib2`. `PrerequisiteChecker.Requires.Wsl` and `CheckWslAsync` are gone; `WxParser.Svc/Program.cs` no longer probes WSL at startup; WxManager's Setup tab drops the WSL checklist row. The `Requires` enum keeps its bit-pattern values so any persisted config referencing old flags cannot collide with new meanings.
- **WxManager Configure tab:** label "wgrib2 WSL Path" → "wgrib2.exe Path". Field renamed `TxtWgrib2WslPath` → `TxtWgrib2Path`. Default suggested value now the Windows path from `WxPaths.Wgrib2DefaultPath`.
- **Ops work (one-time on the HarderWare PC):** download the NOAA Windows wgrib2 build, extract to `C:\HarderWare\wgrib2\`, grant `NT SERVICE\WxParserSvc` RX on that folder, rename the `Wgrib2WslPath` key in `appsettings.local.json` to `Wgrib2Path` (or remove it to let the default apply), redeploy WxParser.Svc, observe the next GFS cycle ingests 121/121 hours.

## 1.3.5 — Retry DB connection at service startup (2026-04-17)

- **WX-28 (partial):** All four services boot with Windows, and after a Windows-Update-driven reboot they race SQL Server's own service start. Before this release, the first `SqlException error 26` ("A network-related or instance-specific error occurred while establishing a connection to SQL Server … error: 26 – Error Locating Server/Instance Specified") was terminal — `DatabaseSetup.EnsureSchemaAsync` threw, each service's outer `try/catch` logged `ERROR Fatal error during startup`, and the services exited without restarting. The 2026-04-17 overnight log showed WxMonitor.Svc died at 04:00:15 and again at 04:32:06 from exactly this pattern; WxParser, WxReport, and WxVis had the same stack traces. When the operator logged in at 12:34, all four came back up successfully on the next manual start, confirming the post-reboot race.
- **Fix:** `DatabaseSetup.EnsureSchemaAsync` now retries transient SQL Server connection errors according to a configurable schedule. Defaults are 12 attempts with delays 5 s, 10 s, 20 s, 30 s, 30 s, 30 s, 30 s, 30 s, 30 s, 30 s, 30 s — roughly 5 minutes of patience before giving up. Each transient failure logs at `WARN` (`Database not ready (attempt N/12): … — retrying in Xs.`); only after every attempt has failed does the service throw `DatabaseUnavailableException` and let the existing outer catch log `ERROR` so Windows SCM recovery actions can restart it.
- **Transient vs. permanent classification.** Only `SqlException` numbers known to indicate a connection-layer condition are retried: –2 (timeout), 20, 26, 40, 53, 64, 121, 233, 258, 1205, 1222, 10053, 10054, 10060, 10061, 11001. Login failures (18456), permission errors, schema conflicts, and malformed configuration all propagate immediately so real bugs still fail fast instead of spinning for five minutes.
- **Scope — all four services.** `WxParser.Svc`, `WxReport.Svc`, `WxMonitor.Svc`, and (now) `WxVis.Svc` all route DB-schema setup through the retry path. WxVis.Svc previously skipped `EnsureSchemaAsync` entirely and allowed its workers to crash with unhandled exceptions if SQL was not yet reachable; it now performs the same startup check as the other three.
- **Configurable without rebuild.** New `Database:StartupRetry` section in `appsettings.shared.json`:
    ```json
    "Database": {
      "StartupRetry": {
        "MaxAttempts": 12,
        "DelaySecondsSchedule": [ 5, 10, 20, 30, 30, 30, 30, 30, 30, 30, 30 ]
      }
    }
    ```
    Any missing element falls back to the in-code default. The delay schedule wraps on its last element, so `MaxAttempts` can be increased without also lengthening the array.
- **First-run database creation is preserved.** `db.Database.EnsureCreatedAsync` (which connects to `master` to create the `WeatherData` database on first run) is inside the retry loop, so new-developer installs where SQL Server is up but the database has not yet been created still work end-to-end — the retry simply waits for SQL Server to answer, then `EnsureCreatedAsync` creates the database and the schema in the normal way.
- **Deferred to follow-up WX-28 PRs:** declaring `DependOnService=MSSQL$SQLEXPRESS` on each service (belt-and-suspenders with this retry), the full Windows service-configuration audit for `INSTALL.md`, and moving `WxParser.Svc` off the personal login it currently runs under.

## 1.3.4 — Retry with exponential backoff on transient upstream fetch failures (2026-04-16)

- **WX-20 / WX-21:** The METAR, TAF, and GFS fetchers previously treated every non-2xx HTTP response and every network-level exception as a terminal failure — `catch (Exception) → Logger.Error → return` or `break`. This overnight produced 12 `ERROR` log entries from `MetarFetcher` (upstream HTTP 502 Bad Gateway / 504 Gateway Time-out from aviationweather.gov) and one from `GfsFetcher` (SSL handshake failure for a specific GFS forecast hour). WxMonitor's alert pipeline treats every `ERROR` as actionable, so transient upstream hiccups were paging the operator for conditions that would have self-resolved within seconds.
- **Fix:** new `HttpFetchRetry.GetStringWithRetryAsync` extension method in `MetarParser.Data` wraps `HttpClient.GetStringAsync` with a 3-attempt exponential-backoff retry (2 s → 4 s → 8 s). Transient failures — HTTP 5xx, 429, SSL/TLS handshake errors, network-level `IOException`, request-timeout `TaskCanceledException` — are retried and each retry logs at `WARN`. Only when all three attempts have failed does the caller log at `ERROR`. Permanent failures (4xx other than 429) throw immediately without retry, so `GfsFetcher`'s existing 404/301/302 treatment ("forecast hour not yet published, stop the loop") is preserved exactly.
- **Applied to:** `MetarFetcher.FetchUrlAndInsertAsync`, `TafFetcher.FetchAndInsertAsync` (companion fix — same aviationweather.gov upstream, same failure pattern), and `GfsFetcher`'s per-forecast-hour `.idx` download. Call sites are one-liners: `await httpClient.GetStringWithRetryAsync(url, "METAR")` / `"TAF"` / `$"GFS f{fhStr} index"`.
- **Context:** these errors are *partially* related to WX-19 (the wrong-station fallback from earlier today) in that they contributed noise to the incident-triage process, but they were never the root cause of the wrong-station bug. Handling them cleanly reduces the signal-to-noise ratio of the WxMonitor alert stream enough that the planned WX-25 rate-based alerting becomes much easier to calibrate.

## 1.3.3 — Defensive idempotent ALTER for Metars/Tafs ReceivedUtc (2026-04-16)

- **WX-22:** The `ReceivedUtc` column on `Metars` and `Tafs` has been a NOT NULL EF property populated by the mappers since commit `a70d81f` (2026-03-30), but no corresponding idempotent `ALTER TABLE ... IF NOT EXISTS` block was ever added to `DatabaseSetup.EnsureSchemaAsync`. Fresh installs pick up the column via EF Core's initial `OnModelCreating`, but a fresh clone on a PC holding an older backup, or any future container/cloud deployment that restores from pre-2026-03-30 data, would see the mapper try to write a column that doesn't exist.
- **Fix:** Added two idempotent `ALTER TABLE ... ADD [ReceivedUtc] datetime2 NOT NULL CONSTRAINT ... DEFAULT SYSUTCDATETIME()` guards in `DatabaseSetup.EnsureSchemaAsync`, matching the pattern used for the WX-13 country/region columns, `Municipality`, `AlwaysFetchDirect`, and `LastMetarIcao`. The DEFAULT only fires to backfill pre-existing rows at ALTER time; EF always supplies an explicit value on insert, so it's a no-op for new rows.
- **Context:** Today's WX-19 investigation relied heavily on `ReceivedUtc` to separate station-outage causes (KAUS silent for 6 h) from fetcher-outage causes (would show as large ObservationUtc→ReceivedUtc lag). The column's diagnostic value is real; this PR just closes the schema-migration gap so that value survives a restore or a fresh deploy.

## 1.3.2 — Geographic nearest-neighbour METAR fallback (2026-04-16)

- **Bug fix (WX-19):** when the recipient's preferred METAR station(s) had no data within the last 3 hours, `WxInterpreter.GetSnapshotAsync` previously fell back to "the single most recently inserted METAR anywhere in the database." Every recipient in the same cycle received the same fallback regardless of geography, and the chosen station drifted as unrelated stations reported — producing observations from Earlton, Ontario (CYXR) for an Austin recipient and Mineola, TX (KJDD) for a Spring, TX recipient this morning.
- **New fallback behaviour:** the interpreter now picks the geographically *nearest* station with a recent METAR, capped at a 50 km (≈30 mi) radius, on a per-recipient basis. Anchored on the recipient's own lat/lon. A lat/lon bounding-box derived from the radius prefilters candidates so the per-row haversine runs only on the handful of stations actually in range.
- **No qualifying station → forecast-only report with an honest note.** If no station within the radius has recent data, the snapshot is still built but carries `ObservationAvailable = false` and an `ObservationUnavailableNote`. The Current Conditions section is rendered as a short italic paragraph explaining that no recent observation is available from a station within about 30 miles, and the report continues with the TAF and GFS forecast sections. The report footer swaps the station/timestamp line for "No current observation". The recipient is only skipped entirely when none of METAR, TAF, and GFS produced data.
- **Change-detection safety.** Observation-less snapshots cannot reliably feed into significant-change detection (the observation fingerprint fields would default to "calm / good visibility / no phenomena" and could fire a false "conditions cleared" alert). `ShouldSend` now suppresses the change-triggered branch when `ObservationAvailable` is false; `RecipientState.LastSnapshotFingerprint` and `LastMetarIcao` are left untouched on forecast-only sends so change-detection resumes cleanly against the last genuine observation when data returns.
- **Fallback warning now includes distance.** The existing "preferred station(s) had no data — fell back to KXYZ" warn now reports the fallback station's distance from the recipient in statute miles, e.g. *"fell back to KHYI (18 mi away)"*. A distinct warn is emitted for the observation-less path so forecast-only sends show up clearly in the log.

## 1.3.1 — Forecast temperatures on two labeled lines (2026-04-15)

- **Extended Forecast table column reformatted** (WX-14). Previously the daily forecast cell read e.g. `85°/72°F`, with the unit suffix ambiguously applying to only the second value. The cell now contains two labeled lines separated by `<br/>` — `High: 85°F` above `Low: 72°F` — with explicit unit suffixes on both. The column header changes from `High/Low` to `Temperatures` to match the new cell format.
- Implemented as a prompt-only change in `WxReport.Svc/ClaudeClient.cs`; no model, schema, or data-pipeline changes.

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
