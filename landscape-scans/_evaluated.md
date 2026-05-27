# Prior evaluations

Tools and approaches that have been evaluated and either adopted, deferred, or
declined. The monthly landscape scan reads this file (STEP 2 of the routine prompt)
and avoids resurfacing declined items unless their named re-evaluate trigger has
fired.

## Declined

### Sentry + Seer Agent observability — declined 2026-05-27 (per WX-89)

Surfaced by the 2026-05 scan as filling a "no observability layer in the baseline" gap.
Reality check: WxServices already has a substantive observability stack.

**Reason.** `WxMonitor.Svc` provides log-scanning + heartbeat-staleness + METAR-freshness
email alerts with per-finding cooldowns; `log4net` writes per-service local logs;
OpenTelemetry SDK exports metrics from all four services to an OTLP endpoint at
`http://localhost:4318/v1/metrics`; the `observability/` directory carries a
docker-compose Grafana stack with provisioned dashboards. Sentry's marginal value
over this would be APM/latency and trace aggregation, neither of which is Sentry's
strongest suit. The agent's "no observability layer" framing was a false positive;
the broader audit of existing-stack vs. modern-tooling alternatives is tracked in WX-89.

**Re-evaluate if any of these triggers fire:**

- A WxServices production incident shows up in symptoms WxMonitor cannot detect
  (slow degradation without log errors; intermittent failures across services that
  would benefit from aggregated error fingerprinting).
- Sentry adds first-class Windows-service heartbeat or background-worker liveness
  checks comparable to WxMonitor.Svc's mechanism.
- WxServices grows past a handful of services or moves to multi-host deployment,
  where local log-file scanning becomes operationally awkward.
- The WX-89 audit concludes that Sentry/Seer fills a gap the current stack does not.

### Atlassian Rovo Dev code review — declined 2026-05-26

Evaluated alongside the existing CodeRabbit Pro subscription. Declined for a solo-dev
hobby project.

**Reason.** $20/dev/mo on top of existing CodeRabbit Pro; no free tier for Jira Free
customers; Rovo's marketed edge (Jira-AC validation) doesn't apply because WX tickets
aren't structured into PR descriptions in a Rovo-parseable way; no independent
C#/.NET review-quality benchmark exists.

**Re-evaluate if any of these triggers fire:**

- Atlassian extends free Rovo credits to Jira Free / Standard sites.
- Independent benchmark published that includes Rovo and tests C#/.NET review quality.
- Paul changes Jira tier (Standard / Premium / Enterprise).
- Paul starts structuring acceptance criteria into PR descriptions in a way Rovo could parse.
