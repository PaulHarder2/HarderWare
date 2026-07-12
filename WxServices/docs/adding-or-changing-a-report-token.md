# Adding or changing a report vocabulary token

This runbook covers what happens — and the decisions you must make — when you **add** a new token to
the report vocabulary (the `LanguageTemplates` phrase set, keyed by the `ReportTokens.Tok`
contract), **or change an existing token's phrase** (a *relabel* — §6, which has the opposite failure
mode and is easy to get wrong). It is the token-side companion to [`enable-a-new-language.md`](enable-a-new-language.md)
(which covers standing up a *whole* new language) and to the rendering/localization architecture in
[`DESIGN.md`](../DESIGN.md) (`StructuredReportRenderer`, the WX-171 token contract, the WX-256
soft-token model, the WX-172/WX-250 generation-and-top-up pass).

The worked example throughout is **WX-265**, which renamed the three daypart tokens to
`DayPart1..4` and added `DayPart1` ("early hours") for the 00:00–06:00 block.

> **The one-sentence version.** A migration seeds the new token for **English only**; every other
> enabled language is *missing* it until the WX-250 top-up generates it **one language per cycle** —
> and whether that gap **suppresses** those languages' reports or **degrades silently** is your
> Required-vs-Soft decision.
>
> **Changing an existing token is the opposite problem.** Top-up won't touch it — the target rows
> already *exist*, carrying the **old** phrase — so nothing suppresses, nothing regenerates, and
> every non-English language silently keeps shipping the stale translation until you fix each row by
> hand (**§6**).

---

## 1. What the migration seeds — and what it doesn't

The report vocabulary is **en-seeded** (WX-251): the seed/rename migration writes only the English
(`LanguageId 37`) rows. Every other enabled language's rows are **generated at runtime**, not
seeded. So the instant your migration deploys:

- **English** has the new token immediately.
- **Every other enabled language** (es, eo, de, …) is now **missing** it — it has no row for that
  token yet.

Two mechanical rules the migration must honor (both fail-closed if skipped):

1. **Add the `Tok` constant** for the token. The renderer may reference *only* `Tok.*` constants,
   and a build-time parity gate (`TokSeedParityTests`) asserts `Tok` matches the en seed exactly —
   so the constant and the seed row must land together.
2. **A rename touches _two_ tables.** Token keys live in both `LanguageTemplates` (the per-language
   phrases) *and* `PromptGlossaryTokens` (the WX-238/WX-244 language-neutral anchor registry). The
   glossary registry is validated fail-closed against the `Tok` contract at load
   (`LanguageTemplateStore` drops any glossary row naming an unknown/renamed token with a loud
   ERROR), so a rename that updates only `LanguageTemplates` will silently break daypart anchoring.
   Rename both. (See WX-265's `RenameDaypartTokens` migration for the `RenameToken` helper that does
   both, and the `SeedTemplateStore` rename-parser that keeps the parity gate green *without*
   editing the historical seed migration.)

A data-only token change (add/rename rows, no column/table change) is **not a schema change** — do
**not** bump `SchemaVersion`.

---

## 2. The decision that matters: Required or Soft?

Every token is **Required** unless you explicitly list it in `Tok.Soft`. The completeness/send
gates key on `Tok.Required` (`= Tok.All − Tok.Soft`), so the choice has a real operational cost the
day you deploy:

| | **Required** (default) | **Soft** (opt-in, WX-256) |
|---|---|---|
| A language missing it | **suppressed** — its recipients get *no* report (fail-closed; no silent English substitution) | **still sends** — the renderer degrades gracefully |
| Deploy behavior for other languages | a **suppression window** until top-up fills each | **zero suppression window** (fill happens in the background) |
| Use when | the renderer *consumes* the token and its absence would break/omit rendering | the token is cosmetic, or *not yet consumed* by the deterministic renderer |

**How to choose:** ask *"does the deterministic renderer fetch this token today?"*
- If **yes** and a missing value would render wrong/blank → **Required** (accept and manage the
  window, §3).
- If **no** — cosmetic (like noon/midnight, WX-256) or dormant until a later ticket consumes it →
  **Soft**, and it "deploys with no suppression window (targets render the fallback while top-up
  fills them)" (DESIGN.md, the WX-256 soft-token paragraph).

> **WX-265's call.** `DayPart1` is *not* fetched by the renderer yet — the 00:00–06:00 block stays
> clock-bound ("00-06") by the WX-190 design until WX-264 decides how the narrative consumes it. By
> the rule above that argues for **Soft**. We chose to leave it **Required** anyway, because there
> were **no scheduled sends remaining that day** (so the window was harmless) and the 5-minute cycle
> fills all languages in minutes — cheaper than carrying a `Soft` entry we'd have to revisit when
> WX-264 lands. **This is a judgment call, not a rule:** if you deploy a Required-but-unconsumed
> token during active sending hours, prefer Soft.

---

## 3. Managing the suppression window (for a Required token)

The window lasts from deploy until each language has the token. To shrink or neutralize it, in
order of preference:

1. **Make it Soft** (§2) — eliminates the window entirely. Best when the token is unconsumed.
2. **Deploy when no scheduled sends are pending** — the window becomes harmless because nothing
   would send anyway (the WX-265 choice). Check the recipients' `ScheduledSendHours` against the
   current local time; unscheduled updates only fire on a genuine forecast change.
3. **Manually pre-fill** — *not currently possible.* The WxManager **Vocabulary** tab edits
   *existing* per-language rows only; it cannot add a token a language is missing (its grid loads
   `WHERE IsoCode = <target>`, and `Token` is read-only). So a brand-new token can't be hand-keyed
   before top-up creates the row. (Surfacing missing baseline tokens in that tab is a possible
   future enhancement.)

---

## 4. How top-up fills the other languages (WX-250)

Each report cycle, the report worker scans **every enabled language** for missing baseline tokens
and spends **at most one Claude generation call per cycle** — filling **only the missing subset**
for the **one** highest-priority language, deferring the rest to later cycles. Priority order:

1. a formerly-**READY** language now missing a **HARD** (Required) token — it's actively suppressing
   live reports, so it goes first;
2. a fresh **PENDING** enable (no recipients yet);
3. a prior **FAILED** attempt.

A language missing only **Soft** tokens is deprioritized (it's still sending), but still gets filled
eventually. So *N* enabled non-English languages missing the token ⇒ roughly *N* cycles to fill all
of them, one per cycle. At the production 5-minute cycle that's minutes, not hours.

---

## 5. Operator loop: watch the log, then QA each language

Top-up is what *creates* the row; once it exists, the Vocabulary tab can *review and edit* it. So
the loop after deploying a new token is:

1. **Tail the WxReport log** — `C:\HarderWare\Logs\wxreport-svc.log` — and watch for the WX-250
   fill lines (these are the fingerprints to grep for):

   | Log line | Meaning |
   |---|---|
   | `WX-250: generating N missing token(s) for '<iso>' (<Language>)…` | this cycle is filling `<iso>` now |
   | `Template generation for '<iso>' produced N token(s)` | the fill validated and persisted |
   | `WX-250: deferring generation of '<iso>' (N missing token(s)) to a later cycle (one generation per cycle)` | queued behind this cycle's one slot |
   | `WX-250: '<iso>' generation FAILED — <reason>` | transient failure; retried next cycle |
   | `WX-172/WX-250: '<iso>' already has a complete template set — marked READY without generation` | nothing missing; free READY stamp |

2. **For each language as it fills,** open **WxManager → Vocabulary**, select that language, find the
   new token's row, and **verify the generated phrase reads correctly** — edit it in place and Save
   if not. (Optionally press **Rerun QA** to re-audit the language, WX-235.) Read the generated
   phrase with appropriate suspicion, especially for low-resource languages like Esperanto where the
   generator/judge is weakest (see `enable-a-new-language.md` §"Reading the judge with appropriate
   suspicion").

When every enabled language shows the token with a phrase you're satisfied with, the rollout is
complete.

---

## 6. Changing an existing token's phrase (a relabel)

Everything above is about *adding* a token. **Relabeling** an already-seeded token — changing its
English phrase without changing its key — is a different operation with the **opposite failure
mode**, and it is easy to get wrong because the top-up machinery (§4) does **not** help you.

The worked example is **WX-284**, which relabeled `WhatsChangedLabel` from "What's changed:" to "Why
this update:" — reframing a change-*list* header into a single-*reason* label.

### Why top-up won't save you

Top-up (§4) fills tokens a language is **missing**. After a relabel, every enabled language **still
has** the token — carrying its **old** translation. The completeness gate keys on *absence*
(`MissingTokens` / `Tok.Required`, `ReportWorker.cs`), and nothing is absent, so none of the §3–§5
dynamics fire:

- **No suppression window** — the rows exist, so every language keeps sending.
- **No top-up fill, no WX-250 log lines** — nothing is "missing," so nothing regenerates.
- **Every non-English language silently ships the stale phrase**, indefinitely, until a human changes
  it.

This is the inverse of adding a Required token, which fails **loud** (suppression + an ERROR
WxMonitor alerts on). A relabel fails **silent**: valid-looking, correctly-rendered text that is
simply **out of date** — and, if the *meaning* changed (as in WX-284: a list header → a single
reason), now semantically **wrong** against the new intent, not merely old wording.

> **Why there is no target-language migration.** Target rows are **not seeded** — they live only in
> the DB (en-only seeding, WX-251; DB is truth). The migration relabels the one **en** row; the
> target rows are curated data. A DB rebuilt from migrations would *generate* the targets fresh from
> the **new** en baseline via top-up, so they'd be correct — the stale-row problem exists **only in
> an already-populated production DB**, which is exactly the DB you must hand-fix.

### The procedure

1. **Relabel the `en` row via migration.** Use the `Relabel(mb, "en", "<Token>", "<new phrase>")`
   helper (see WX-284's `WX284RelabelChangeBand`), keyed on `(IsoCode, Token)` so it's robust to the
   row's surrogate id. `SeedTemplateStore.ParseRelabels` already parses this shape, so the parity
   gate and goldens track the new en value automatically — **no historical-seed edit and no
   `SchemaVersion` bump** (data-only). Update any golden/snapshot that asserts the phrase.

2. **Fix every enabled target language's row — this is the step nothing does for you.** The rows
   already exist, so (unlike a *brand-new* token, §3 point 3) you **can** edit them in place:
   - **WxManager → Vocabulary tab** — select each language, find the token, correct the phrase, Save.
     The sanctioned operator path.
   - Or a **direct `LanguageTemplates` UPDATE** for phrases you can author with confidence.
   - **Only ship a foreign phrase you can validate.** Ones you can't (low-resource languages, or
     anything you're unsure of) go through QA — don't hand-ship them. A wrong phrase in front of a
     live recipient is worse than a short delay (the same gate as the WX-168 per-language lexicons).

3. **Rerun QA for each changed language.** Press **Rerun QA** (WxManager, WX-235) or run the
   `TranslationQa` tooling so the judge re-audits the new phrase **against the new meaning** — a
   relabel that changed intent invalidates the prior audit, not just the prior wording.

4. **Verify the whole set.** Confirm every enabled language now carries the intended phrase and none
   was missed — the fastest check is a direct query:

   ```sql
   SELECT l.IsoCode, lt.Phrase
   FROM LanguageTemplates lt JOIN Languages l ON l.Id = lt.LanguageId
   WHERE lt.Token = '<Token>' AND l.IsEnabled = 1 ORDER BY l.IsoCode;
   ```

   (Beware the sqlcmd codepage fold when eyeballing non-ASCII phrases — it silently folds accented
   characters to ASCII; dump as UTF-8 / `varbinary` hex if a phrase looks mangled.)

### Relabel checklist

- [ ] `en` relabel migration using a `Relabel()` shape `ParseRelabels` recognizes; goldens/snapshots
      updated; **no `SchemaVersion` bump** (data-only).
- [ ] **Every enabled target language** row corrected (Vocabulary tab or DB `UPDATE`) — top-up will
      **not** do this for you.
- [ ] Foreign phrases either author-validated or routed through QA (don't hand-ship the unsure ones).
- [ ] **Rerun QA** per changed language, judged against the token's **new meaning**.
- [ ] Final `SELECT … WHERE Token = …` confirms all enabled languages carry the intended phrase.

---

## 7. Checklist (adding a token)

- [ ] Add the `Tok` constant (and doc-comment it if the name isn't self-evident).
- [ ] Decide **Required vs Soft** (§2); if Soft, add it to `Tok.Soft`.
- [ ] Migration seeds the **en** row; a rename updates **both** `LanguageTemplates` **and**
      `PromptGlossaryTokens`.
- [ ] Data-only ⇒ **no `SchemaVersion` bump**; keep the parity gate green (extend
      `SeedTemplateStore` if you introduce a new migration shape, as WX-265 did for renames).
- [ ] If **Required**, pick a **deploy window with no pending scheduled sends** (§3), or accept/plan
      the suppression window.
- [ ] After deploy, **watch the log** (§5) and **QA each language's** new phrase in the Vocabulary
      tab as top-up fills it.

---

## Related

- [`enable-a-new-language.md`](enable-a-new-language.md) — the whole-language lifecycle (generation,
  enable-time QA, recipient assignment).
- **WX-256** — the soft-token severity model (`Tok.Required = Tok.All − Tok.Soft`); the precedent
  for shipping a new token with no suppression window.
- **WX-265** — worked example (daypart rename + `DayPart1`); the `RenameToken` migration helper and
  the `SeedTemplateStore` rename-parser.
- **WX-284** — worked example for **§6** (relabeling `WhatsChangedLabel` "What's changed:" → "Why this
  update:"); the `Relabel()` migration helper and the `SeedTemplateStore.ParseRelabels` parser. The
  en-only relabel stranded the curated es/de/da/eo/sq rows — the case that motivated §6.
- **WX-264** — will consume `DayPart1` in the narrative; may revisit its Required-vs-Soft status.
- **DESIGN.md** — `StructuredReportRenderer` localization, the WX-171 parity gate, the WX-256
  soft-token send-gate model, and the WX-172/WX-250 generation-and-top-up pass.
