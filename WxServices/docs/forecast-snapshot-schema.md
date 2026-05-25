# Forecast snapshot body schema (v1)

**Schema version:** 1
**C# source of truth:** `MetarParser.Data.Entities.ForecastSnapshotBody`
**Storage:** the `Body` column of the `ForecastSnapshots` table (`nvarchar(max)`)

## Purpose

A snapshot is the structured representation of what WxReport told a recipient at the moment of a commit. Later report cycles diff against the most recent snapshot for the recipient's station to decide whether the forecast has been invalidated. Introduced under WX-76 for the WX-47 rearchitecture; see WX-47 for the umbrella and the motivating incident.

## Top-level shape

```json
{
  "schemaVersion": 1,
  "blocks": [ /* one or more block objects */ ]
}
```

| Field | Type | Notes |
| --- | --- | --- |
| `schemaVersion` | `int` | Current value: 1. Bumps only when the body shape changes in a way that requires version-aware reads. |
| `blocks` | array of block | Ordered earliest first. Up to 24 blocks covering a six-day horizon. |

## Block shape

```json
{
  "startUtc": "2026-05-26T00:00:00Z",
  "skyState": "overcast",
  "obscuration": "none",
  "temperatureCelsius": { "min": 8.2, "max": 14.7 },
  "windKt":            { "min": 5,   "max": 12  },
  "gustOutlook": "occasional",
  "precipExpectation": "likely",
  "precipPhenomenon": "rain",
  "severeFlag": false,
  "visibilityExpectation": "good"
}
```

| Field | Type | Allowed values / notes |
| --- | --- | --- |
| `startUtc` | ISO 8601 UTC | Block start. Always aligned to 00/06/12/18Z. End is implicitly `+6h`. |
| `skyState` | enum | `clear`, `partly_cloudy`, `mostly_cloudy`, `overcast`. Cloud-cover state only — fog and other obscurations live in `obscuration`. |
| `obscuration` | enum | `none`, `fog`, `haze`, `smoke`, `dust`. Independent of `skyState`. |
| `temperatureCelsius` | `{ min, max }` of `number` | Celsius. Recipient unit preferences are applied at email-render time. |
| `windKt` | `{ min, max }` of `integer` | Knots. Matches the METAR/TAF native unit. |
| `gustOutlook` | enum | `none`, `occasional`, `frequent`. Categorical to fit Claude's working-memory framing — specific gust values may surface in the email when warranted. |
| `precipExpectation` | enum | `none`, `possible`, `likely`, `certain`. |
| `precipPhenomenon` | enum or absent | `rain`, `thunderstorm`, `mixed`, `snow`, `freezing_precip`. Must be absent (omitted from JSON) exactly when `precipExpectation == "none"`. `freezing_precip` covers freezing rain, freezing drizzle, and sleet — bucketed together because they are equally road-slickening and equally safety-critical. |
| `severeFlag` | `bool` | Safety-critical flag. The rules that set it live in WX-81 (significance-tier prompting); WX-76 reserves the column. |
| `visibilityExpectation` | enum | `poor`, `reduced`, `good`. Three tiers chosen for plans-affecting granularity at 6-hour resolution. |

## Why JSON in a single column (and not a relational decomposition)?

Discussed at WX-76 grooming and recorded in the ticket. No obvious query need for per-block relational access yet, and the "Claude's working memory" framing of WX-47 favours a single self-describing artifact. Revisit only if relational queries become necessary downstream.

## Versioning

`schemaVersion` appears on the entity row (the `SchemaVersion` column) and inside the body. Both are written together. The column is authoritative when scanning many rows without parsing; the body is authoritative for stand-alone diagnostics. Bump both together if the body shape ever changes incompatibly.

## Out of scope for WX-76

- Population of the body (the GFS-to-provisional builder is WX-77).
- Persistence wiring at send time (WX-78).
- Claude reading/writing the snapshot (WX-79).
- The threshold rules that set `severeFlag` (WX-81).
