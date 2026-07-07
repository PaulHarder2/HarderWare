# Jira Label Taxonomy (WxServices / WX project)

**This document is the single source of truth for the labels we have sanctioned** on WX Jira issues. A label is not sanctioned unless it appears here.

Jira Cloud has no admin page for a canonical label list — labels spring into existence the first time they are applied, and the registry issue **[WX-37](https://harderware.atlassian.net/browse/WX-37)** exists only to seed Jira's autocomplete pool. WX-37 is the mechanical autocomplete mirror; **LABELS.md is authoritative** and wins on any disagreement. Established as source of truth 2026-07-04.

## Labels *and* epics — they answer different questions

We use both, deliberately:

- **Epic** = the one body of work a ticket advances ("which project"). A single parent.
- **Labels** = orthogonal *facets* — component, work-character, source. Many per ticket.

Rule of thumb: a ticket gets an **Epic parent** when a body-of-work home exists. A standalone ticket with no natural epic must still carry **≥1 component + ≥1 work-character label**, so it's findable. A cross-cutting ticket gets an epic parent *and* the extra facet labels (that's the case labels handle that a single-parent epic can't).

**Exemption — process / meta docs.** A ticket whose deliverable is a *process or meta doc* — WORKFLOW.md, LABELS.md, CONVENTIONS.md, and the like — has no fitting component: the component dimension names runtime sub-systems, and a process-doc edit touches none of them. Such a ticket is **exempt from the ≥1-component rule** when it carries **`ai-collab`** (which already marks it as work *about the workflow itself*); it still carries `docs` for work-character. This keeps the ceremony from outweighing the point. *Established 2026-07-07 (WX-272).*

## The three dimensions

Labels are **orthogonal** across three dimensions. A typical issue carries one to three total: usually one component + one work-character, occasionally a meta label.

### Component / area — *what sub-system is touched*

| Label | Meaning |
| --- | --- |
| `wxparser` | WxParser.Svc — METAR/TAF + GFS data fetch |
| `wxreport` | WxReport.Svc — Claude report generation + email send |
| `wxmonitor` | WxMonitor.Svc — log/heartbeat/DB health monitoring |
| `wxvis` | WxVis.Svc + WxVis Python — map and meteogram rendering |
| `wxinterp` | WxInterp — forecast interpolation / derived fields |
| `wxmanager` | WxManager — management GUI |
| `wxviewer` | WxViewer — map viewer desktop app |
| `database` | SQL Server / EF Core / migrations / schema |
| `claude-integration` | Anthropic Claude API pipelines and prompting |
| `config` | Settings layering, service configs, appsettings |
| `infrastructure` | Service install, Windows account setup, startup order, deployment |

### Work character — *what kind of change*

| Label | Meaning |
| --- | --- |
| `ui` | User-facing visual or interaction change |
| `reliability` | Startup correctness, retries, error handling, graceful degradation |
| `observability` | Logs, OTel metrics, traces, dashboards |
| `performance` | Speed, throughput, resource usage |
| `security` | AuthN/AuthZ, secrets handling, input validation |
| `refactor` | Internal restructuring with no behavior change |
| `tech-debt` | Explicit repayment of prior shortcuts |
| `docs` | DESIGN.md, README, INSTALL, code comments as primary deliverable |
| `quick-win` | Small, self-contained, good fit for fragmented time |

### Source / meta — *where this came from*

| Label | Meaning |
| --- | --- |
| `coderabbit` | Raised or refined by CodeRabbit review |
| `ai-collab` | Meta work about the AI-collaboration workflow itself |
| `needs-design` | Wants DESIGN.md / discussion before coding |
| `incident` | Came from something broken in running services |
| `standing-epic` | Marks a standing / perpetual Epic (never closed). Applied *only* to the standing epics — WX-39, WX-137, WX-153, WX-254, WX-255, WX-270, WX-271 — for dashboard filtering. |

## Rules

- **Lowercase, hyphen-separated.** Never `WxManager` or `wx_manager`.
- **Prefer reuse** over invention. Check for an existing label before adding one.
- **Sanction a new label by adding it here first** — this doc is the source of truth. Then apply it once on the WX-37 registry issue so it enters Jira's autocomplete pool. A label absent from LABELS.md is not sanctioned, whatever autocomplete happens to offer.

## In-practice reconciliation (2026-07-04 audit)

Labeling had lapsed — 81% of standalone tickets carried no label, and nothing was labeled after WX-225. When reviving, reconcile these drift items:

- `claude` appears in use → canonical is **`claude-integration`**; migrate.
- `bug` / `regression` appear in use but are **not sanctioned as labels** — they duplicate the Jira **issue type** (Bug). Use the issue type for a defect; do not apply these labels to new issues.
- `wxinterp` is added to the component list here (a real sub-system WX-37 omitted).
