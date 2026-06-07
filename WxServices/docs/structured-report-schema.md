# Structured report body schema (v3)

**Schema version:** 3 (lockstep with the forecast snapshot body — see Versioning)
**C# source of truth:** `MetarParser.Data.Entities.StructuredReportBody`
**Storage:** the `StructuredReport` column of the `CommittedSends` table (`nvarchar(max)`, nullable)

## Purpose

The unit-neutral, language-complete representation of one report's *content*, emitted by the Claude reconciliation alongside the forecast snapshot (WX-128). Where the snapshot body captures forecast *state* (6-hour blocks), this captures what the report *says*: a salience-ranked changes array (language-free facts) plus a language-keyed narrative whose quantities are substitution tokens. A deterministic renderer (WX-129) turns it into each recipient's email — units, locale, language — with no further LLM call, which is what makes the one-call-per-locality economics of WX-123 work.

During the additive transition (until WX-130 rewires the loop), the column is persisted-but-unread: `email_body` remains the sent artifact.

## Top-level shape

```json
{
  "schemaVersion": 3,
  "changes": [ /* zero or more change objects, most important first */ ],
  "narrative": {
    "en": { /* sections */ },
    "es": { /* sections */ }
  }
}
```

| Field | Type | Notes |
| --- | --- | --- |
| `schemaVersion` | `int` | Current value: 3. |
| `changes` | array of change | Reader-relevant differences versus the prior committed forecast, most important first. Empty when nothing changed. |
| `narrative` | object | One key per ISO 639-1 language code. Which languages must be present is a per-call contract (the locality's recipients' distinct languages); validation fails closed on a missing one. |

## Change shape

```json
{
  "tier": "plans",
  "phenomenon": "thunderstorm",
  "direction": "appearing",
  "window": { "startUtc": "2026-06-08T21:00:00Z", "endUtc": "2026-06-09T03:00:00Z" },
  "quantities": [ { "kind": "gust", "value": 30 } ],
  "summaryToken": "ch1"
}
```

| Field | Type | Allowed values / notes |
| --- | --- | --- |
| `tier` | enum | `safety`, `plans`, `ambient` — the WX-81 significance hierarchy. |
| `phenomenon` | enum | `rain`, `thunderstorm`, `mixed`, `snow`, `freezing_precip`, `wind`, `wind_shift`, `fog`, `haze`, `smoke`, `dust`, `temperature`, `severe`. `wind_shift` is the WX-111 vector case. |
| `direction` | enum | `appearing`, `strengthening`, `weakening`, `clearing`, `shifting`. Appearing/clearing carry the directional asymmetry. |
| `window` | `{ startUtc, endUtc }` | ISO 8601 UTC window the change affects. |
| `quantities` | array | `{ kind, value }` pairs in the kind's canonical unit (below). May be empty for purely categorical changes. |
| `summaryToken` | string | `ch1`, `ch2`, … in array order; unique. Ties the change to its sentence in every language's `changeSummary`. |

## Narrative sections (per language)

| Field | Type | Notes |
| --- | --- | --- |
| `changeSummary` | string or null | Prose for the "What's changed:" band; null when there is no band. The **only** section where `{chN}` anchors appear. |
| `currentConditions` | string | Prose for the Current Conditions section. |
| `extendedForecast` | string | Prose for the Extended Forecast section. |
| `closing` | string | Prose for the "In summary:" closing. |

## Token grammar

Validated by `ReportTokens` at serialize and deserialize time; any brace-delimited span that matches no form is rejected, so a typo'd token can never reach a recipient as literal text.

| Token | Meaning | Canonical unit |
| --- | --- | --- |
| `{q:temp:33.5}` | temperature | degrees Celsius |
| `{q:wind:22}` | sustained wind | knots |
| `{q:gust:30}` | gust | knots |
| `{q:pressure:1013.2}` | pressure | hPa |
| `{q:precip_mm:12}` | liquid-equivalent accumulation | millimetres |
| `{q:time:2026-06-08T21:00:00Z}` | an instant | ISO-8601 UTC; rendered in locality-local time |
| `{ch1}` | change anchor | renders to nothing; sentence ↔ change link |

Values use a period decimal separator regardless of language; the renderer formats per recipient locale. Prose adjacent to tokens must be unit-neutral ("highs near {q:temp:33.5}", never "in the low 90s") — enforced by prompt. One sub-case **is** validated deterministically: a literal unit word or symbol immediately after a token (`{q:gust:41} kt`, `{q:gust:30} nudos`) is rejected, because the renderer appends the unit at substitution time and the prose copy would double it ("47 mph kt"). Claude produced exactly this on its first live call against the v3 schema, so the rule earned a validator, not just a prompt line.

## Invariants (enforced at serialize and deserialize)

- At least one narrative language; keys are well-formed ISO 639-1 codes.
- `currentConditions`, `extendedForecast`, `closing` non-blank in every language.
- `summaryToken`s well-formed and unique.
- Every change's anchor appears in every language's `changeSummary`, and no anchor dangles — the structural "no change goes unnarrated, in any language" guarantee.
- Anchors appear only in `changeSummary`; every token parses.

Per-call (reconciler-level) contracts, on top of the intrinsic ones: every *requested* language present, and each requested language's narrative clears a WX-120-style visible-length floor.

## Versioning

`schemaVersion` moves in **lockstep** with `ForecastSnapshotBody.SchemaVersionCurrent` (decided at WX-128 grooming): the two bodies travel in the same tool_use envelope, so a shape change to either bumps both. Born at v3.

- **v3** (WX-128): initial shape.
