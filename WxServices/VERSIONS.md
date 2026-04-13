# Version History

This project follows [Semantic Versioning](https://semver.org/): **MAJOR.MINOR.PATCH**.
Patch releases are bug fixes, minor releases introduce new features, and major releases are reserved for large changes.

| Version | Commit  | Date       | Summary |
|---------|---------|------------|---------|
| 1.0.0   | 7a2a268 | 2026-04-07 | Initial versioned release |
| 1.0.1   | ef49de4 | 2026-04-11 | Version display and startup logging |
| 1.0.2   | d55384e | 2026-04-12 | Meteogram temp-unit bug fix; log format cleanup |
| 1.1.0   | f6fd52c | 2026-04-12 | Multi-zoom map rendering with interactive zoom/pan |

---

## 1.0.0 — Initial versioned release (2026-04-07)

- Introduced system-wide version number via `Directory.Build.props`, replacing per-project versioning
- All six programs (four services, WxManager, WxViewer) share a single version source of truth
- Git commit hash embedded as `AssemblyMetadata("GitCommit", ...)` for runtime diagnostics

## 1.0.1 — Version display and startup logging (2026-04-11)

- Version number shown in WxManager and WxViewer custom title bars (styled smaller and dimmer)
- Version and short git commit hash logged at startup for all six programs (e.g. `WxReport.Svc 1.0.1 (commit ef49de4) starting.`)
- Suppressed the SDK `+hash` suffix from `InformationalVersion` so title bars and email footers display a clean version string
- Added Versioning section to DESIGN.md

## 1.0.2 — Meteogram temp-unit bug fix; log format cleanup (2026-04-12)

- **Bug fix:** Recipients configured for Celsius were receiving Fahrenheit meteograms because (1) PNG filenames did not include the temperature unit, causing C and F renders to overwrite each other, and (2) `FindMeteogramAbbrevPath` matched only on ICAO + timezone, ignoring `TempUnit`. Fixed by adding a `{F|C}` tag to filenames and a `tempUnit` parameter to the finder.
- Standardized all ReportWorker recipient log lines to the format `{Id} {Email} ({Name})`
- GFS forecast summary log now respects the recipient's temperature unit preference

## 1.1.0 — Multi-zoom map rendering with interactive zoom/pan (2026-04-12)

- **Multi-zoom rendering pipeline:** `synoptic_map.py` and `forecast_map.py` accept `--zoom-level N`. Each level doubles figure size and halves station density. Font sizes and contour line widths scale proportionally. DPI is 150 for z1, 100 for z2+ to manage file sizes.
- C# workers (`AnalysisMapWorker`, `ForecastMapWorker`) loop over configurable zoom levels (default 3) per render cycle. New `WxVisConfig.ZoomLevels` property. Filenames include `_z{N}` suffix. `MapFileScanner` updated with new regex patterns and `ZoomPaths` dictionary.
- **WxViewer zoom/pan UX:** Mouse-wheel zoom toward cursor, click-drag pan, double-click to reset. Zoom-in at 3.0× crossfades to the next higher-resolution image; zoom-out at 1.4× swaps back. Link Panes toggle synchronizes zoom/pan between both panes. Reset Zoom button and zoom-level indicator (Z1/Z2/Z3) in toolbars.
- CONUS map extent expanded from (-126, -65, 22, 50) to (-136, -60, 17, 55) to prevent Lambert Conformal clipping at edges
- Low-quality bitmap scaling during active interaction for responsiveness, restored to high-quality after 200 ms idle
- Maximized window constrained to working area to avoid taskbar overlap
