# Prior evaluations and scan decisions

What we decided about every monthly landscape scan, and where each tool stands today.

**Read this file two ways.**

- **[Decisions by scan](#decisions-by-scan)** — indexed by **report**. *What did we
  conclude about the August scan?* One sub-section per scan, newest first, one row per
  finding. **From `2026-09.md` onward** each scan report ends with a `**Decisions.**`
  footer linking back to its section here; reports up to and including `2026-08.md`
  predate that and carry no such link, so reach them from this file rather than the
  other way round.
- **[Standing dispositions](#standing-dispositions)** — indexed by **tool**. *Where do
  we stand on Sonnet 5, and what would reopen it?*

**The monthly scan routine reads BOTH halves, with different force.** The dispositions
bind it: it will not recommend adopting what is already adopted, and will not resurface
a deferred or declined item unless that entry's named re-evaluate trigger has fired. The
register informs it: it reads the most recent scans' rows so it does not re-raise in
different words something already judged *already done* or *not credible* — verdicts
that carry no disposition entry and would otherwise be invisible to it.

⚠️ **That behaviour lives in the routine's prompt, which is cloud-hosted and NOT in this
repository.** The prompt granting it was updated under **WX-389**, alongside this file;
the routine's identifier is recorded there rather than here (see the scope note). If the
two ever drift, the paragraph above is a claim about a system nobody reading this repo
can see — check the routine before trusting it.

**The register indexes; the entries reason.** A verdict's rationale — and every date,
error code and figure it rests on — lives in exactly one place: the disposition entry. A
scan row gives a short verdict and a pointer. **A row must never restate a fact from the
entry**, or a correction has to be made twice and the second copy is the one that rots.

> ## ⚠️ Scope — this file is PUBLIC
>
> `PaulHarder2/HarderWare` is a **public** repository. The generated monthly reports
> (`landscape-scans/20*.md`) are excluded from CodeRabbit review via `.coderabbit.yaml`
> `path_filters` and have **no reviewer but us**. **This file is reviewed**, as of
> WX-389 — but CodeRabbit reviews *changes*, so any line carried forward untouched has
> still never been read by anything but us. **Assume standing content is unreviewed.**
>
> **NEVER here — put it in the Jira ticket, which is private:** credentials, API keys or
> tokens of any kind; private hostnames and internal IP addresses; **internal resource
> identifiers** (routine/trigger ids, environment ids, account or connector UUIDs);
> third-party pricing given in confidence; and anything about Paul's personal life,
> finances, employment or health.
>
> **Explicitly FINE here, and deliberately so:** Paul's name and his quoted decisions.
> He owns this repository, every commit carries his name, and he publishes under it — so
> there is nothing to protect. More to the point, **an attributed decision is a stronger
> record than an anonymous one**: "Paul's decision, 2026-08-01" plus his reasoning is
> what makes an entry a decision rather than somebody's opinion, and it tells a future
> reader whose call it is to reverse. Well-known default endpoints (`localhost:4318`,
> the OpenTelemetry default) are fine for the same reason — they are documentation, not
> disclosure.
>
> *(An earlier version banned "personal content" and "endpoints" flatly. CodeRabbit read
> it exactly as written and asked for the attribution and the OTel default port to be
> stripped — a fair reading of a rule that said more than it meant. The rule was the
> defect, not the content.)*
>
> When in doubt, put it in the Jira ticket instead — **Jira is private, this is not.**

---

## Decisions by scan

Newest first. **One row per finding, including findings that produced no disposition
entry** — *"already done"* and *"we didn't believe it"* are decisions too, and the
standing-disposition sections cannot hold them.

`Entry` points at the disposition entry carrying the reasoning; **—** means the verdict
in the row is the whole record.

**Adding a scan's rows is the last step of reacting to it**, not a tidy-up afterwards. A
finding with no row has not been dispositioned, and a scan with no sub-section here has
not been worked at all. That makes this register its own completeness check — the
property a tool-indexed list cannot have.

### 2026-09

Report: [`2026-09.md`](2026-09.md) · scanned 2026-09-01 · 3 findings.
**One trigger fired and was answered, one trigger-check ran and changed nothing, and one
was already satisfied.** No ticket was opened against any finding. Decisions taken by
Paul on 2026-09-01; recorded under WX-492.

| Finding | Verdict | Entry |
|---|---|---|
| 1. Sonnet 5 deferral trigger fired — pricing case reversed | **Deferral upheld.** The trigger genuinely fired: $2/$10 is now the permanent standard price, so Sonnet 5 is ~13% cheaper effective rather than ~30% dearer. That reverses **reason 2 of four** and touches none of the others. **Cost stopped being an argument AGAINST migrating; it did not become an argument FOR migrating now** | [Claude Sonnet 5 for the WxServices runtime](#sonnet-5-runtime) |
| 2. Claude Fable 5 — Opus 5 adoption trigger check | **Checked, no change.** Opus 5 retained for Claude Code; 2× the price is decisive on its own | [Claude Code on Claude Opus 5](#opus-5-claude-code) |
| 3. .NET 8.0.30 patch | **Already satisfied** — measured on PaulOmniBook 2026-09-01, `dotnet --list-runtimes` reports `Microsoft.NETCore.App 8.0.30`. The scan predicted this from the 8.0.29 pattern and predicted right; **the measurement is what settles it, not the prediction** | — |

### 2026-08

Report: [`2026-08.md`](2026-08.md) · scanned 2026-08-01 · 4 findings + 1 note.
**Two of the four were already done before the scan ran.** Of the remaining two, one was
deferred and one declined — see the rows, and the entries they point at, for why. The two
error classes that let the scan report finished work as open are being fixed in **WX-389**.

| Finding | Verdict | Entry |
|---|---|---|
| 1. Claude Opus 5 for Claude Code | **Already in place** when the scan ran — see the entry for why the scan did not know that | [Claude Code on Claude Opus 5](#opus-5-claude-code) |
| 2. Sonnet 5 + sampling-temperature removal | **Deferred** — see the entry | [Claude Sonnet 5 for the WxServices runtime](#sonnet-5-runtime) |
| 3. CodeRabbit Post-Merge Actions | **Declined** — see the entry | [CodeRabbit Post-Merge Actions](#coderabbit-post-merge-actions) |
| 4. .NET 8.0.29 patch | **Already satisfied** — 8.0.25 *and* 8.0.29 both installed, target framework `net8.0` confirmed. The scan's own check (`dotnet --version`) reports the **SDK** and cannot answer the question it was posed against; `dotnet --list-runtimes` can. **Do not re-raise without running the discriminating check first** | — |
| Note: `claude-opus-4-1` retirement (Aug 5) | **Confirmed clear** — no source or config file references it: `grep -rn "claude-opus-4-1" WxServices/ --exclude='*.md' --exclude-dir=bin --exclude-dir=obj` returns zero. ⚠️ **EXCLUDE documentation; do not allowlist extensions.** Unscoped, the grep matches our own prose about the retirement — a self-falsifying check. But an allowlist (`--include=*.cs --include=*.json`) silently misses `*.config`, `*.props` and anything we adopt later. Excluding docs covers every file type, now and in future | — |

### 2026-07

Report: [`2026-07.md`](2026-07.md) · scanned 2026-07-01 · 2 findings + 1 note.
Dispositioned retroactively on 2026-08-01, when its Sonnet 5 finding was settled
together with the 2026-08 repeat.

| Finding | Verdict | Entry |
|---|---|---|
| 1. Sonnet 5 migration — promo window + breaking code change | **Superseded by the 2026-08 deferral.** This report is the source of the facts the entry rests on — quote the entry, not this row | [Claude Sonnet 5 for the WxServices runtime](#sonnet-5-runtime) |
| 2. Deprecation check | **Confirmed clear** | — |
| Note: Claude Code default → Sonnet 5, and the June recommendation to move to Opus 4.8 *"still applies"* | **Opus tier retained** for long-horizon multi-file work — Sonnet 5 not taken as the Claude Code default. The outstanding June→Opus-4.8 recommendation this note carried was **overtaken** by the move to Opus 5 | [Claude Code on Claude Opus 5](#opus-5-claude-code) |

### Scans before 2026-07

**Not retro-filled, deliberately.** The register starts where the practice starts;
reconstructing verdicts from memory would manufacture decisions nobody took.
[`2026-05.md`](2026-05.md) and [`2026-06.md`](2026-06.md) remain readable on their own,
and the two 2026-05 entries under **Declined** below carry their own dates and reasoning.

---

## Standing dispositions

Indexed by tool. Every entry states what was decided, why, and what would make us
revisit — an entry with no re-evaluate trigger is a decision nobody can reopen on
evidence, so every entry has one.

⚠️ **Each entry carries an explicit stable anchor** (`<a id="…">`) that does **not**
encode its verdict or date. Entries are expected to move between Adopted, Deferred and
Declined when a trigger fires, which rewrites the heading — the stable anchor is what
stops every inbound register link breaking when that happens. **Keep the id when you
move an entry; change only the heading.**

⚠️ **A re-evaluate trigger MUST NOT BE SATISFIED ON THE DAY IT IS WRITTEN.** A trigger
describing a state that already holds has already fired, so the entry it guards is not
guarded at all — and it fails *open*, silently, in the direction that reads as the
mechanism working. Most triggers therefore name a **change**; a **future date** is also
legitimate, since it is not yet satisfied and will fire exactly once. What is forbidden
is a condition that is true the moment you write it.

⚠️ **And a trigger set must be able to fire for the SUCCESSOR, not only for the thing it
names.** An Adopted entry bars the scan from re-recommending what we already run. If its
triggers cover only changes to *that* item, a newer product that supersedes it cannot be
raised at all — and the guard then suppresses precisely the kind of finding that created
the entry. Every Adopted entry needs a "something supersedes this" trigger.

### Adopted

Already in the stack. The scan must not recommend adopting these again, though a
*change* to one may be reportable.

<a id="opus-5-claude-code"></a>

#### Claude Code on Claude Opus 5 — in place as of 2026-08-01

**Raised by:** [2026-08](#2026-08) finding 1; [2026-07](#2026-07) note;
[2026-09](#2026-09) finding 2 (trigger check).

`claude-opus-5` became Claude Code's default Opus model in v2.1.219 (2026-07-24) at
unchanged pricing — $5/$25 per MTok, 1M context. Verified in use 2026-08-01: the running
session reports model id `claude-opus-5[1m]`.

**Reason.** Same price as Opus 4.7 and 4.8, and it is the CLI's own default. No known
breaking change for the coding-assistant role. The 2026-06 scan's recommendation to move
to Opus 4.8 is superseded rather than contradicted.

**Note for the scan.** The 2026-08 scan reported this as an open action while it was
already in place, because the routine prompt's hardcoded stack inventory still named an
older model. This entry exists partly to stop that repeating.

✅ **TRIGGER CHECK, 2026-09-01 (WX-492) — THE FIRST BULLET BELOW FIRED, AND THE ANSWER IS
NO CHANGE.** Claude Fable 5 (`claude-fable-5`, released 2026-06-09) is Anthropic's
highest-capability widely released model, which fires *"otherwise the better tier for
long-horizon multi-file work."* **Opus 5 is retained.**

🔑 **$10/$50 against $5/$25 — 2× — is decisive on its own**, and the check deliberately
does **not** rest on the two weaker arguments the scan offered. *(Its refusal-handling
argument is the weaker one: a refusal is a documented `stop_reason` with a first-class
server-side fallback, so it is a handled condition rather than a disqualifier. Recorded
so a later reader does not treat it as load-bearing and then retire a correct conclusion
on finding it refutable.)*

⚠️ **AND THE ROUTINE WAS LATE — THE SAME BLIND SPOT THE NOTE ABOVE RECORDS, IN NEW
CLOTHES.** Measured 2026-09-01, **immediately before this entry was written**: Fable 5
shipped 2026-06-09 and appeared **zero** times in the 2026-05 through 2026-08 reports,
and nowhere in this file. ⚠️ **The second half of that stopped being true the moment this
block was committed — it is a record of the state before the 2026-09 entry, not a claim a
reader can re-run.** *(Written in the present tense first, which made it false on arrival:
the sentence asserted an absence into the file that was ending it. Caught by CodeRabbit on
PR #231.)* This entry's trigger was
only written 2026-08-01, so no earlier scan was strictly obliged to check it — but a new
top-tier model going unmentioned across two scans is worth a routine-side fix. **Not
actioned here; a candidate to discuss, not a filed ticket.**

**Re-evaluate if:**

- **A model THIS ENTRY HAS NOT ALREADY EVALUATED supersedes `claude-opus-5` as Claude
  Code's default Opus model, or is otherwise the better tier for long-horizon multi-file
  work.** The evaluated set, which is the whole of it:

      claude-fable-5   evaluated 2026-09-01, DECLINED - see the trigger check above

  🔴 **THE EXCLUSION IS LOAD-BEARING: UNBOUNDED, `claude-fable-5` SATISFIES THIS CLAUSE
  FOREVER.** It was evaluated and declined, so an unbounded trigger lets every future scan
  re-raise a model this register has already dispositioned — defeating the file's own
  promise that *"a decision recorded there will not be raised at you again."*
  ⚠️ **ADD TO THE SET when a model is evaluated and declined here — do NOT convert this to a
  RELEASE-DATE bound.** A date was written first and is the wrong shape: it would also
  excuse a model released *before* the date that nobody ever evaluated — `claude-mythos-5`
  is exactly that case today, released and never assessed here. **An enumerated set can
  only exclude what someone actually looked at; a date excludes by accident of timing.**
  *(Finding: CodeRabbit, PR #231. The date-versus-set correction is mine, on re-reading my
  own fix before committing it.)* This is the trigger that
  matters, and it is the one the first draft of this entry lacked: without it, an Opus 6
  shipping at the same price with `claude-opus-5` still Active fires nothing, and the
  scan is barred from raising the very upgrade this entry records us having made.
- A later Opus release **changes** the price or context window for this tier.
- Anthropic **announces** deprecation of `claude-opus-5`.

### Deferred

Examined and consciously postponed. **Do not resurface before the named trigger fires.**

<a id="sonnet-5-runtime"></a>

#### Claude Sonnet 5 for the WxServices runtime — deferred 2026-08-01, upheld 2026-09-01

**Raised by:** [2026-07](#2026-07) finding 1; [2026-08](#2026-08) finding 2;
[2026-09](#2026-09) finding 1.

**Not adopted, and no ticket opened** — deliberately. Paul, 2026-08-01: *"nothing we
really need to act on now… That will come later when Sonnet 4.6 sunsets."*

**Reason — four things, in the order that decides it.**

1. **No forced clock today, but the runway is shorter than it looks.**
   `claude-sonnet-4-6` is Active, earliest retirement **2027-02-17** (2026-07 scan,
   first-party sourced) — **200 days, about six and a half months**, from this deferral.
   Nothing compels the move *now*, and reason 3 means it needs a working window rather
   than a weekend, so this is a decision to revisit deliberately rather than drift past.
2. **The cost case is weak-to-negative, which is the counterintuitive part.** The
   introductory rate saves ~13% effective **only through 2026-08-31**; from 2026-09-01
   Sonnet 5 is ~30% *more* expensive effective, because its tokenizer yields ~1.3× the
   tokens for the same text. Migrating "to save money" becomes a permanent raise inside
   a month. *(Tokenizer factor is the scans' claim, consistent across 2026-07 and
   2026-08, but not independently verified against the pricing docs — do that before
   acting on it.)*

   > 🔴 **REASON 2 IS DEAD AS OF 2026-09-01, AND IT IS THE ONLY ONE OF THE FOUR THAT
   > MOVED.** The scheduled increase to $3/$15 **did not happen.** The pricing page now
   > carries an explicit note that the $2/$10 rate *"is now the standard price"* and that
   > the previously scheduled increase *"will not occur."* So Sonnet 5 is **~13% cheaper
   > effective, permanently** — not ~30% dearer. **Read the paragraph above as a record
   > of what was true on 2026-08-01, not as current guidance.**
   >
   > ✅ **AND THE TOKENIZER FACTOR IS NOW VERIFIED FIRST-HAND, which discharges this
   > entry's own standing instruction to do exactly that before acting.** The pricing
   > page states that the newer tokenizer *"produces approximately 30% more tokens for
   > the same text"*, and adds that *"the exact increase depends on the content and
   > workload shape."*
   >
   > ⚠️ **SO ~13% IS A POINT ESTIMATE ON AN APPROXIMATION, AND BREAK-EVEN SITS AT
   > EXACTLY 1.5× — for input and output alike** ($2 × 1.5 = $3; $10 × 1.5 = $15). At
   > 1.3× we save ~13%; at 1.5× we save nothing; above it the migration is a permanent
   > raise. 🔴 **OUR OWN RATIO IS UNMEASURED**, and the translator emits non-English
   > prose, which is precisely where a tokenizer ratio drifts furthest from a headline
   > figure. `count_tokens` against real reconciler and translator payloads would settle
   > it. **Nobody has run it, and no trigger depends on it** — this is a note for
   > whoever opens the migration, not an open action.
   >
   > 🔑 **WHAT THIS CHANGES, STATED PRECISELY: cost stopped being an argument AGAINST
   > migrating. It did not become an argument FOR migrating now.** Reasons 1, 3 and 4
   > stand untouched, and 3 and 4 are the ones carrying the real cost.

3. **It is a breaking API change, not a config swap — and it is PLATFORM-WIDE, not a
   Sonnet 5 quirk.** A non-default `temperature` returns **HTTP 400** on Sonnet 5 **and
   on Opus 4.7+** (2026-07 scan). `ClaudeClient.cs` sends one on both call paths:
   `ReconcilerTemperature = 0.5` (line 110) and `TranslatorTemperature = 0.2` (line
   118), used at lines 213 and 405. Both must be removed before the model id can move.
   **Read this as a platform direction**: any future model is likely to carry the same
   constraint, so "wait for a model that restores it" is probably not a strategy.
4. **Losing the sampling temperature is two decisions, not one.** It is a *variance*
   control, not a *reasoning* control, so "a better model compensates" does not apply
   evenly. The reconciler's 0.5 guards against explanatory overreach — a reasoning
   failure, which a stronger model plausibly does reduce. The translator's 0.2 guards
   against drift and paraphrase — a sampling-variance failure, which it does not. The
   two constants also move in **opposite** directions from the default: the reconciler's
   was deliberately raised so the prose breathes.

**Re-evaluate if any of these triggers fire.** ⚠️ **"None of them is satisfied" was true of
the 2026-08-01 BASELINE and is no longer true: the pricing trigger FIRED on 2026-09-01 and
is marked spent below.** As written on 2026-08-01: none was satisfied by the state recorded
above — 2027-02-17 is that state, not a trigger. Most name a *change*; the
second is a *future date*, which is legitimate because it is not yet true and will fire
exactly once:

- **`claude-sonnet-4-6` moves to Deprecated, or a retirement date EARLIER than
  2027-02-17 is announced.** The primary trigger, and the reason the deferral is safe:
  the monthly scan already reads the model deprecation page, so it fires without anyone
  remembering to look.
- **2026-12-01 arrives and the model is still Active.** A calendar backstop, because the
  migration needs a working window and reason 1's runway is 200 days, not years. This
  trigger exists so the deferral cannot quietly become a decision by default.
- Anthropic **restores** a settable `temperature`, or documents an equivalent variance
  control, on the current model family. *(Per reason 3 this is a platform-wide
  restriction, so treat it as unlikely and do not wait on it.)*
- Sonnet 5's **standard, post-promotional** pricing changes such that it is no longer
  more expensive effective than Sonnet 4.6. *(Worded against the standard rate on
  purpose: through 2026-08-31 the introductory rate already makes Sonnet 5 ~13% cheaper,
  so a trigger phrased about "pricing" alone would be satisfied the day it was written —
  the failure the rule above forbids.)*
  ✅ **FIRED 2026-09-01 AND ANSWERED — deferral upheld; see the block under reason
  2. This trigger is SPENT and cannot fire again.** The three triggers above it are
  unfired and unchanged, and the 2026-12-01 backstop still stands.
*(A fifth trigger stood here — "token spend grows enough that a percentage difference
becomes material" — and was removed. It named no threshold and no observer, so nobody
could ever determine whether it had fired, which makes it indistinguishable from having
no trigger at all. The 2026-12-01 backstop already forces a deliberate revisit. Add a
figure and it can come back.)*

**Do not re-raise this as a cost saving.** ⚠️ **The REASON changed on 2026-09-01 and the
INSTRUCTION did not.** The saving no longer inverts — at the headline tokenizer ratio it
is real and permanent. But a ~13% saving was never what was holding this migration back,
and it answers neither reason 3 nor reason 4. **A cost argument is not a sufficient
reason to open this work; the retirement clock and the 2026-12-01 backstop are.**

### Declined

Evaluated and rejected. **Do not resurface before the named trigger fires.**

<a id="coderabbit-post-merge-actions"></a>

#### CodeRabbit Post-Merge Actions — declined 2026-08-01

**Raised by:** [2026-08](#2026-08) finding 3.

CodeRabbit runs automated actions when a PR merges into the default branch — file a Jira
ticket, open a GitHub Issue, append a changelog entry, or post a Slack summary — each
previewed as a checkbox in the PR walkthrough. Included in the existing CodeRabbit Pro
subscription. Pitched by the scan as reducing manual overhead on the WX workflow.

**Reason — strongest first. Paul's decision, 2026-08-01.**

1. **A ticket filed by CodeRabbit cannot carry our taxonomy, and the taxonomy is most of
   what a ticket is here.** Paul: *"I don't actually want CodeRabbit filing Jira tickets
   on my behalf, because we have a ticket taxonomy that it knows nothing about."* Issue
   type (Epic / Story / Bug / Task), the parent epic, priority — which **inherits from
   the parent**, not from a severity label — and labels from the sanctioned set in
   `LABELS.md` are all invisible to it. Every filed ticket would need re-typing,
   re-parenting, re-labelling and estimating, which is the whole job.
2. **Merge-time is the wrong moment for a ticket in this workflow.** A ticket must exist
   *before* the branch does — `WORKFLOW.md` §2 requires the WX key in the branch name.
   So by the time a PR merges, its ticket has existed for the whole life of the work.
   Anything auto-filed then is either a duplicate of that ticket, or a follow-up
   CodeRabbit invented on its own judgement. The first is noise; the second is a tool
   deciding what work should happen next.
3. **An auto-filed ticket is ungroomed by construction** — no scope, no acceptance
   criteria, no estimate — and nothing enters In Progress here without those.

**What CodeRabbit already gives us instead, which is the part that matters.** Measured
2026-08-01 across four merged PRs, spanning the categories *Functional Correctness*,
*Stability & Availability*, *Data Integrity & Integration* and *Maintainability & Code
Quality*, and the severities Trivial / Minor / Major. Every finding carries: category,
severity, an effort hint, a title, **the reasoning stated against the specific code
decision**, `file:line`, and a concrete suggested fix — frequently compilable code.

That covers a groomed ticket's two hardest fields. The *why* is there, and **the
suggested fix is the acceptance criterion**. What is missing is exactly the taxonomy
layer in reason 1, which a person who knows this project adds in about a minute.

⚠️ **So the live problem is RETRIEVAL, not content.** `WORKFLOW.md` §9a exists because
findings hide in collapsed sections (*Outside diff range*, *Duplicate*, *Nitpick*) and
`check-cr.sh` does not show them — on WX-235 a Major concurrency finding sat in an
outside-diff comment the poller never displayed. **The information CodeRabbit writes is
already sufficient; our reading of it is lossy.** Any effort here belongs on reading the
whole report, not on asking the tool for more.

*(An earlier version of this entry declined partly on the ground that "three of the four
actions do not apply". That was wrong: Post-Merge Actions takes **natural-language
instructions**, and the four are examples, not a menu. It also treated a 403 on
`docs.coderabbit.ai/changelog` as durable evidence when the outage was transient — the
page reads fine. Both errors are recorded rather than quietly removed, because the
decline stands on other grounds and a reader deserves to know which ones moved.)*

**Re-evaluate if any of these triggers fire:**

- **Jira gains a way to accept a ticket that arrives without our taxonomy and have it
  typed, parented, prioritised and labelled on the way in** — an intake template, a
  default parent per repository, or an automation rule. That is the condition reason 1
  turns on, and it is about **our** side, not CodeRabbit's.
- We **adopt** a generated changelog in place of the hand-curated `VERSIONS.md` flow.
- We **start using** Slack or GitHub Issues for this project.

⚠️ **A trigger that was here and has been REMOVED, because it was never a trigger.** It
read *"CodeRabbit adds an action that files a ticket for unresolved or declined review
findings"* — and since actions are authored as **natural-language instructions**, that
one could be written today. It was satisfied the moment it was written, which is the
failure the rule at the top of this section forbids, and it was the trigger this entry
singled out as the one worth watching.

**The underlying want was real and is not lost: a finding we decline or defer leaves no
tracker trace unless one of us hand-files it.** That is a gap in *our* process, not a
missing vendor feature, and it should be tracked as its own ticket rather than parked
behind a disposition that bars the scan from mentioning it.

<a id="sentry-seer"></a>

#### Sentry + Seer Agent observability — declined 2026-05-27 (per WX-89)

**Raised by:** 2026-05 scan.

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

<a id="rovo-dev"></a>

#### Atlassian Rovo Dev code review — declined 2026-05-26

**Raised by:** 2026-05 scan.

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
