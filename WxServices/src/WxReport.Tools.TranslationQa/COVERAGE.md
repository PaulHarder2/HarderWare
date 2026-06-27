# Translation-QA exemplar coverage (WX-215)

What the two exemplar scenarios in `Exemplars.cs` make the renderer emit, so the WX-214 judge
audits (nearly) the whole controlled vocabulary. This matrix is **generated from rendered output**,
not asserted: set `WXQA_DUMP_DIR` and run `ExemplarFixtureTests` to regenerate the HTML, then read
the *Reported Conditions* rows and the forecast *Conditions* column.

- **A** = warm/convective frontal passage (`WarmConvective`)
- **B** = winter/frozen storm (`WinterFrozen`)

The forecast vocabulary comes from each scenario's provisional blocks (one render). The observed
vocabulary needs the alternate observations — one report shows one observation, so each row below is
a separate `obs` re-render of the same forecast.

## Forecast band — precipitation × likelihood

| Phenomenon | possible | likely | expected | where |
|---|---|---|---|---|
| Rain | ✓ | ✓ | — (see note 1) | A |
| Thunderstorm (storms) | ✓ | ✓ | ✓ | A |
| Severe storms (convective) | — (note 2) | ✓ | ✓ | A |
| Severe weather (non-convective) | — (note 2) | ✓ | — (note 2) | A |
| Freezing rain | ✓ | ✓ | ✓ | B |
| Wintry mix | — (note 3) | ✓ | ✓ | B |
| Snow | ✓ | ✓ | ✓ | B |
| Clear-and-dry | ✓ (both A & B, post-system) | | | A, B |

## Sky (forecast emits bare words; observations add the ceiling variants)

| Token | where |
|---|---|
| Clear | A, B (obs + clear-and-dry) |
| Partly cloudy | A (obs) |
| Mostly cloudy (bare) | A "mid-deck broken" |
| Overcast (bare) | B "mid-deck overcast" |
| Low / High mostly cloudy | A "low mostly" (light rain showers), "high broken" |
| Low / High overcast | A "low overcast" (light rain), "high overcast"; B "low overcast", "high overcast clearing" |
| Sky obscured | B "dense fog" (vertical visibility) |

## Visibility + obscuration

| Token | where |
|---|---|
| Good | A/B clear & high-deck obs |
| Hazy | A "heavy rain"; B "freezing rain"/"light drizzle"/etc. |
| Reduced | B "moderate snow" |
| Poor | B "heavy snow" (0.3 mi, no obscuration → distance band) |
| Fog | B "dense fog" |
| Mist | B "primary" (freezing drizzle + mist) |
| Haze | A "hazy, pre-frontal" |
| Smoke | A "wildfire smoke aloft" |

## Wind

| Token | where |
|---|---|
| Calm | A "clear and calm"; B "dense fog" |
| Variable | A "partly cloudy, variable wind"; B "cold and clear" |
| Direction at speed | nearly every obs |
| Gusting | A "primary" (g32); B "heavy snow" (g32) |

## Observed weather (Reported-Conditions Weather row)

| Token | where |
|---|---|
| Thunderstorm | A "primary" |
| Light rain / Heavy rain / Rain showers | A "light rain" / "heavy rain" / "light rain showers" |
| Light drizzle | B "light drizzle" |
| Freezing drizzle | B "primary" |
| Freezing rain | B "freezing rain" |
| Snow / Light snow / Heavy snow / Snow showers | B "moderate snow" / "light snow" / "heavy snow" / "snow showers" |
| Wintry mix (sleet / ice pellets) | B "sleet — ice pellets" |
| Fog / Mist / Haze / Smoke | B "dense fog" / B "primary" / A "hazy" / A "wildfire smoke" |

## Day-parts

Overnight (00-06), morning (06-12), afternoon (12-18), evening (18-24) all appear in both forecast
grids (the scenarios are anchored at day-1 00:00 so no band is trimmed).

## Change-band vocabulary (developing / intensifying / easing / ending; temp- & wind-change nouns)

Realized by the **WX-216 harness**, not the fixtures alone: it diffs each scenario's `Provisional`
against its quieter `Prior` to drive the deterministic change detector. The priors are authored so
the systems register as developing/intensifying and the post-system clearing/warming registers as
easing/ending + a temperature change. Confirming the exact direction words is a WX-216 concern.

## Deliberate omissions (coherence over token-bingo — see WX-215 acceptance)

1. **Rain `expected` (certain)** — both arcs reach "certain" precip convectively (storms) or frozen
   (snow/ice), not as plain certain stratiform rain; forcing a "Rain expected" cell would break the
   arc. The rain noun is audited via rain possible/likely and the observed rain rows.
2. **Severe storms `possible`, severe weather `possible`/`expected`** — a severe threat in a single
   coherent front peaks at likely→expected (storms) or a post-frontal high-wind "likely"; sweeping
   the severe families across every likelihood is not meteorologically natural in one passage. The
   likelihood words themselves are fully audited via the (non-severe) storm family.
3. **Wintry mix `possible`** — the mix is the brief transition zone between ice and snow; it appears
   at likely/expected. A separate "possible" mix cell would be contrived.
4. **Bare moderate "Rain"/"Drizzle"** (observed) — covered as light/heavy/showers/freezing variants;
   the bare moderate noun is the same word and adds no translation-audit value.

These are recorded here rather than forced into the forecasts, per the WX-215 acceptance that the
scenarios read as real progressions. If the judge ever needs a withheld cell, add a third targeted
mini-exemplar rather than distorting A or B.
