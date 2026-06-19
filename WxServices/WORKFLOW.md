# WxServices development workflow

Last updated 2026-06-01.

This document is the authoritative workflow for landing a change in WxServices. It is a snapshot of the rules Paul and Claude have agreed on over time; when something here conflicts with an ad-hoc direction, this document wins unless the conflict is flagged and the document updated.

The flow intentionally trades speed for audit: every change maps to a Jira ticket, every release maps to an immutable commit hash, and every substantive code change has been seen by two reviewers — Claude's `/code-review` first (§7d, as of 2026-05-29), then CodeRabbit as the independent second pass (§9).

## 1. Groom the ticket

**Added 2026-04-23.**

### Issue type

Pick the Jira issue type at grooming time:

- **Epic** — umbrella; decomposes into child tickets. Usually Story-shaped (a capability too big for a single Story), occasionally Bug-shaped (architectural rework that fixes something rather than adding a feature, e.g. WX-47). **Every umbrella is an Epic.**
- **Story** — a user-visible capability or feature.
- **Bug** — a defect to fix.
- **Task** — tooling, infra, cleanup, dependency bumps, maintenance — work that's neither a user-facing capability nor a defect.

Stories, Bugs, and Tasks can stand alone or be subordinate to an Epic. When the same work could plausibly be Story or Task (a small developer-facing improvement), prefer Task — Stories are reserved for end-user-visible capability.

### Grooming proper

Every ticket must be *groomed* at creation or at the latest before it is transitioned from **To Do** to **In Progress**. Grooming is the working conversation that fleshes out motivation, scope, acceptance, and estimate — it surfaces ambiguity before it becomes wasted implementation time, and it builds long-term calibration about how well we predict effort.

A groomed ticket has, at minimum:

- A description that makes the *why* explicit: what need triggered the ticket and what scope it covers (and does not cover).
- Explicit **acceptance criteria** a reviewer can confirm against the merged code. If criteria cannot yet be stated, the grooming is incomplete.
- An **Original estimate** (Jira's native field), in hours, representing our best current guess of combined effort through merge. Rough is better than absent.

**Epics** (the umbrella variant) follow a variant of the rules above:

- Their Jira Original estimate field is left empty.
- Their description ends with a **Planning Estimate** section — a rough aggregate hour count (*"hopefully better than order-of-magnitude, but maybe not"*) plus an approximate sub-ticket count.
- Child tickets are created **just-in-time** by default, not at Epic-grooming time. Each child is groomed individually before it is picked up for work. This avoids front-loading grooming sessions for children whose scope isn't yet in focus.
- **Opt-in: up-front child creation and grooming.** When the Epic's Acceptance section already names the children with enough scope to estimate, the children may be created and groomed up-front. The trade is real summed Original estimates on the Epic — better calibration data — against the re-estimation risk that working an early child reshapes a later one. Bounded by a tripwire: if the summed-child Original estimates ever drift more than 25% from the initial aggregate, re-groom the Epic (refresh the Planning Estimate; reconsider whether the decomposition still holds; restructure or add sub-tickets as needed). *Added 2026-05-25 from WX-47, the first Epic to use this opt-in.*

If a ticket reaches §3 (transition to In Progress) without grooming, pause and groom it first. The cost of a grooming conversation is much less than the cost of implementing the wrong thing.

## 2. Branch

Work on a feature branch named `WX-NN-short-description`. The branch name **must** contain the Jira key (`WX-NN`) so the GitHub-for-Jira integration auto-links the branch, commits, and PR to the ticket.

Do **not** commit directly to `master`.

## 3. Transition the Jira ticket to In Progress

**Added 2026-04-18.**

As soon as real work starts on a ticket (branch created, first edit made — whichever comes first), transition the Jira ticket from **To Do** to **In Progress**. If it's already In Progress, skip.

This is cheap board hygiene — a 2-second API call — but it serves two purposes:

- **Honest status for anyone reading the project.** A ticket that's been worked on for an hour but still shows "To Do" is a lie of omission.
- **A cue to keep the human in the loop.** Paul has commented that AI assistants tend to "dumb down the human in the loop" unless the workflow structurally resists that pull. A missed In Progress transition is a small signal that the AI-human pair is drifting into autopilot — catching it early is worth more than the transition itself.

## 4. Commit

Write a full detailed multi-line commit message explaining the *why*, not just the *what*. Include a `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>` trailer so attribution is consistent across the history.

Small unrelated tweaks (typo fixes, log-message cleanups, doc edits) may be bundled alongside the main ticket work rather than split into separate PRs. Use judgment: if the tweak would confuse a bisect or complicate a revert, split it.

**Document off-scope fixes in the ticket.** *Added 2026-06-01.* When a bundled change goes beyond a trivial tweak — fixing an actual bug or defect unrelated to the ticket's scope — add a comment to the active Jira ticket naming the find, the fix, and that it was folded into this PR, and note it in the PR body. Inline cleanup is encouraged (it's cheaper than a follow-up PR), but it must leave a paper trail: otherwise the ticket's history lies about what shipped, the calibration data (what the ticket actually cost) is muddied, and a reviewer can't tell why the diff touched unrelated files. Prompted by WX-81, during which the WX-80 1.8.0 / `Directory.Build.props` version-bump miss was found and corrected inline.

## 5. VERSIONS.md

For any change that bumps the version in `Directory.Build.props`, add a new row to `VERSIONS.md` with the **commit hash left as `_pending_`** and a detailed narrative section below the table. Per semantic versioning:

- **PATCH** — bug fixes with no behavior change for clients.
- **MINOR** — new features with backwards-compatible behavior.
- **MAJOR** — large changes; reserved for significant reshaping.

The hash is filled in later (step 10) once the PR is finalized and CodeRabbit is clean.

Pure-tooling / pure-docs PRs (e.g. a `.coderabbit.yaml` config change, a WORKFLOW.md edit with no accompanying runtime change) do not bump the version. VERSIONS.md tracks runtime releases; a PR that produces byte-for-byte identical binaries should not announce a new version to email recipients or log files.

## 6. Code walkthrough before push

**Added 2026-04-18.**

After all code edits are written — but before running tests or pushing — walk Paul through every changed file: **what changed, why, any surprises**. Scale the depth to the change:

- **Typo fix / one-line change**: one-sentence summary.
- **Single-file feature**: a paragraph per concern.
- **Multi-file refactor** (e.g. WX-33's 14 files): a file-by-file breakdown, optionally grouped by concern ("rename chain", "config-key updates", "doc updates").

The walkthrough is the moment Paul re-enters the loop. It serves three purposes:

- **Catches mistakes before CodeRabbit does**, which saves the CodeRabbit review fee on trivially-caught issues.
- **Keeps Paul engaged in the engineering.** Paul is explicitly concerned that AI coding assistants tend to dumb down the human reviewer over time. The walkthrough is a structural guard against that atrophy — Paul reads what ships in his name.
- **Lets Paul object before the branch grows more commits.** Uncommitted changes can be amended freely; "fix" commits after a premature push clutter history.

Wait for Paul to say "LGTM" or call out specific edits to adjust before proceeding to step 7. Do not start the test run until the walkthrough is accepted.

## 7. Tests before PR (the following sub-steps)

**Added 2026-04-17.**

### 7a. Ask Paul about test coverage

Before pushing, explicitly ask whether the change warrants:

- **(a) new unit tests**,
- **(b) adjustments to existing tests**, or
- **(c) both**.

"Adjustments to existing" is as important as "new" — a behavior change that silently obsoletes a test assertion is a regression hiding behind a green bar.

Some changes (installer scripts, raw config files, prose-only doc edits) cannot be meaningfully unit-tested. That's fine — say so and move on. The phrase *"when it is possible and makes sense"* governs.

**Manual test procedures.** *Added 2026-06-06 (WX-134).* When a change's verification cannot be automated (the WPF apps have no UI test harness and are not even CI-built — see WX-135), the manual verification gets the same rigor as automated tests:

1. Write a **formal test procedure** as a repo document at `WxServices/docs/test-procedures/WX-NN.md` — numbered steps with explicit *Expect:* outcomes. It rides the PR, so the second reviewer reviews the test procedure too, and it remains executable as a regression suite later.
2. Record it in the ticket's two custom fields (both Short Text): **Test Proc** holds the document path (`docs/test-procedures/WX-NN.md`); **Test Result** holds the latest outcome (`PASS yyyy-mm-dd @ <version>` or `FAIL …`). Re-executions overwrite Test Result; history survives in Jira's field-change log.
3. A ticket whose change needs manual verification does not pass §7c until the procedure exists and Test Result records a PASS. (JQL gap-detectors: `"Test Proc" is EMPTY` for missing procedures on UI-touching tickets; `"Test Result" ~ "FAIL"` for open failures.)

**Functional (post-deploy) verification artifacts.** *Added 2026-06-13 (WX-180/WX-181).* The same "ride the PR" rule applies to the §13 deployed-system check, and **this is the step where it is enforced** — because the detail lives in §13, which reads as a *post-deploy* section, it is easy to defer the artifact until after merge and then have to bolt it on retroactively (WX-160's one-time exception became the unintended pattern on WX-180 and WX-181). So, before the first push:

1. If the change warrants functional verification (§13 — anything whose real behavior can only be confirmed against the deployed system: a gate, a suppression, a cost path, an integration), its **procedure `WX-NN.md` and its script `WX-NN-verify.sh` must already exist and ride this PR**, exactly like the manual procedure above. They are review-complete at authoring time: the script embeds the release `VERSION` and `COMPONENTS` it tests (both known once the version bump is in the diff) and resolves its boundary via `deploy-info.sh` (§13). Authoring them now is what lets `/code-review` (§7d) and CodeRabbit (§9) review them, and makes them ready the moment the change deploys.
2. Set the ticket's **Test Proc** to the procedure-doc **path** (`docs/test-procedures/WX-NN.md`) at this point — *never* a prose description and *never* the `.sh` path (the doc references the script). Or set it to the literal **`N/A`** when unit tests fully cover the change and no manual/functional procedure is warranted — distinct from an *exempt* change (point 4), which has nothing to verify and skips this step entirely: `N/A` is a deliberate "covered by unit tests," not a blank field that reads as forgotten. **Test Result** records the PASS later, after the §13 in-production run (legitimately empty until then — a structural pre-deploy run is a `WAIT`, never a PASS).
3. **Smoke-run the script once before pushing.** If this PR creates *or changes* a `WX-NN-verify.sh`, execute it once against the live DB before the push. It resolves to `WAIT` / `PASS` / `FAIL` harmlessly (a pre-deploy or pre-window run is a `WAIT`), and a *script-level* fault — a broken `sqlcmd`/`awk`/quoting error — surfaces immediately. `bash -n` is necessary but **not** sufficient: it validates bash syntax only, so it cannot catch an awk-internal quote break — e.g. an apostrophe in an awk comment closes the single-quoted program, which then truncates and leaks its own source as data (WX-197). Nothing else in the pipeline runs the script — `/code-review` (§7d), CodeRabbit (§9), and CI lint/compile the C#, not the bash — so this run is the only pre-merge guard against a verify script that is itself broken. **And don't take a clean-looking verdict at face value — sanity-check the run's counts** (reports / cycles / exercised) against the actual number of qualifying rows in the DB. A logic bug can under-count without erroring, which reads as a benign `WAIT` rather than a fault — e.g. an in-loop `powershell.exe`/`sqlcmd` draining the `while`-read loop's stdin so only the first candidate row is ever processed (WX-198). A `bash -n` pass plus a no-error run is necessary but still not proof the script counted everything it should.
4. Exempt changes (pure-docs / pure-config / pure-process / pure-tooling — nothing functional to verify) skip this, same as the §7b test-run exemption. (A PR that only *fixes* a verify script still runs point 3 — that script is the change under test.)
5. **Bundled PRs share one procedure.** When several tickets ship in a single PR (allowed — §3), they **share one** procedure + verify script: the script pins the release `VERSION` (its `deploy-info.sh` boundary), **not** a Jira key, so a per-ticket script would be redundant and misleading. Name both artifacts with **every key they cover** — `WX-AAA-BBB-CCC.md` / `WX-AAA-BBB-CCC-verify.sh` — and set **each** participating ticket's **Test Proc** to that one shared path. *Added 2026-06-19 (WX-208), after WX-204/205/206 shipped together (PR #123) with the convention undefined.*

### 7b. Run the full test suite and format check

`dotnet test WxServices.sln` must report zero failures before creating the PR. There are no pre-existing-failure exceptions: any failure must be resolved in this PR or split out into a blocking ticket first.

`dotnet format WxServices.CI.slnf --verify-no-changes` must also exit clean before push. CI runs this on every PR and a format-only failure costs a full CI round-trip plus a CodeRabbit-equivalent cycle on a mistake that is trivial to fix locally. *Added 2026-05-19 after WX-72's first CI run failed on two trailing-newline drifts.*

**Exemption:** when the change cannot meaningfully affect test results — pure-docs PRs (WORKFLOW.md, DESIGN.md, etc.), pure-config PRs (e.g. `.coderabbit.yaml`), pure-asset PRs — skip the test run and note the exemption in the PR body. The format check still applies if any C# file changed, since `dotnet format` is cheap; skip only for diffs that touch no `.cs` files. Same governing phrase as §7a applies: *"when it is possible and makes sense."*

### 7c. Confirm acceptance criteria are met

**Added 2026-04-19.**

Before pushing, read the ticket's **Acceptance** section and explicitly ask Paul whether every listed criterion has been satisfied by the change. Do not assume — say the criteria back and get confirmation.

- If a criterion is unmet because of a mistake (missed scope), fix it before pushing.
- If a criterion is deliberately out of scope for this PR (e.g. the PR is one of several sub-PRs that together satisfy an umbrella ticket, or a criterion was re-scoped during the work), call it out explicitly in the PR body so the reviewer is not left guessing.
- If the ticket has no Acceptance section at all, pause and flag that to Paul before pushing — the ticket itself needs amendment first.

Reason: acceptance criteria are the ticket's explicit contract. Tests verify code correctness; acceptance criteria verify *feature* correctness. Both are needed and neither is a substitute for the other. Skipping this step is the same class of mistake as skipping the code walkthrough — it lets the pair drift into autopilot and ships work whose completeness nobody actually checked.

### Rationale

CodeRabbit costs money per review. Handing a paid reviewer code with test failures wastes both the fee and Paul's follow-up attention. This guardrail keeps the review budget focused on design and correctness issues the tests can't catch.

### 7d. Claude `/code-review` first

**Added 2026-05-29.**

After the §6 walkthrough and the §7a–7c gates — but **before the push opens the PR to CodeRabbit** — run Claude's code-review agents (the `/code-review` skill — the interactive local form, which reviews the working-tree diff with no PR required) and resolve every valid finding. This applies to every PR, including pure-docs ones where the §7b test run is exempt. CodeRabbit (§9) is then the independent *second* reviewer, not the first.

Treat Claude's findings exactly as CodeRabbit's (§9): fix the valid ones, decline the bad ones with a written rationale, don't apply every suggestion reflexively. Note this Claude pass is a *self*-review — the same model family that wrote the code — so it does **not** replace CodeRabbit's cross-model independence; it front-loads the catch so the paid, independent reviewer sees already-cleaned code.

Reason: on WX-100 (2026-05-29) CodeRabbit reviewed and *approved* the first cut, and a Claude `/code-review` pass afterward caught a genuine availability regression CR had missed (a retry-on-timeout change that could stall a report cycle for ~15 minutes and trip the heartbeat-stale monitor). Running Claude first catches that class of issue before a CodeRabbit cycle is spent and before a regression ever reaches an approved state.

## 8. Push and open PR

```bash
git push -u origin WX-NN-short-description
gh pr create --title "WX-NN: short description" --body ...
```

PR title is prefixed with `WX-NN:` for traceability. Body has a `## Summary` section and a `## Test plan` checklist. If the §7b exemption is used (pure-docs / pure-config / pure-asset PR), note that the test suite was skipped and why.

**Immediately after opening the PR, transition the Jira ticket from In Progress to In Review.** This is the mirror of the To Do → In Progress transition in §3: the code-writing phase has ended and the reviewing phase has started, and the Jira board should reflect that. Same board-hygiene and autopilot-tripwire reasoning applies. Added 2026-04-18 after Paul caught it missing from the first draft of this workflow — the ticket introducing the In-Progress-transition rule itself landed in a state that needed the very In-Review transition that rule had forgotten to mention.

## 9. CodeRabbit review

CodeRabbit is the **second** reviewer: Claude's `/code-review` (§7d) runs on the working tree before the PR opens, with its findings resolved first. CodeRabbit (OpenAI-backed) then auto-reviews within ~5 minutes. Address valid findings with follow-up commits on the same branch. Decline bad suggestions with a clear rationale written into a reply comment — don't apply every suggestion reflexively. Example precedent: the `App.UserAgent` suggestion on PR #2 was declined because it would have inverted the dependency layering.

AI-reviewer independence matters here: CodeRabbit is OpenAI, Claude is Anthropic. The two-AI split is deliberate — the Claude pass (§7d) front-loads the catch, but it is a self-review, so CodeRabbit remains the cross-model second opinion on the final diff.

CodeRabbit's behavior is tuned via `.coderabbit.yaml` at the repo root (added in WX-32). The config enables the `assertive` review profile, high-level summaries, Jira-issue linking, effort estimates, and per-path guidance for C#, Python, Markdown, and PowerShell. Tune as needed; changes to the yaml are themselves PRs through this same workflow.

## 10. Hash-fill commit

When CodeRabbit is clean, add a **separate commit** whose sole change is filling in the v1.N.N commit hash in `VERSIONS.md` (replacing `_pending_`). The hash to record is the SHA of the commit that introduced the `Directory.Build.props` version bump — equivalently, the feature branch's HEAD at the moment *before* this hash-fill commit is added. It is never the hash-fill commit itself, and never the merge commit.

This hash-fill commit does **not** gate on a fresh CodeRabbit review. It is a deterministic `_pending_`→hash substitution, and the substantive change has by now cleared both Claude's `/code-review` (§7d) and CodeRabbit. Merge it as soon as **CI** is green; CodeRabbit still posts its quick check on the commit, but we do not wait on a re-review. *Added 2026-05-29 — supersedes the earlier "wait for CR even on the hash-fill" practice, which added latency with no payoff on a mechanical edit.*

Skip this step for pure-tooling / pure-docs PRs that did not bump the version (see §5).

## 11. Merge — always "Create a merge commit"

- **Never "Squash and merge"** — collapses all branch commits into a new SHA, invalidating the hash recorded in `VERSIONS.md`.
- **Never "Rebase and merge"** — replays commits onto master's HEAD, producing new SHAs.
- **Always "Create a merge commit"** — preserves the original feature-branch SHAs as they appear on master, so every `VERSIONS.md` hash resolves to a real commit.

## 12. Post-merge cleanup

1. Delete the remote branch. GitHub's auto-delete (enabled 2026-04-15) handles this automatically; manual `git push origin --delete WX-NN-...` is only needed if auto-delete is disabled.
2. `git checkout master && git pull` locally.
3. Delete the local branch: `git branch -d WX-NN-...`.
4. **Log time spent.** Add a Jira worklog entry recording combined pair-effort from grooming through merge (e.g. `45m`, to 15-minute granularity). This closes the calibration loop with the Original estimate set at grooming time. The dev phase ends at merge, so the worklog is booked here regardless of which transition follows. *Added 2026-04-23.*
5. **Transition the Jira ticket.** If the change warrants functional testing (§13), move it to **In Test** (`Begin Testing`) — it reaches **Done** only once verification passes. If it's exempt (pure-docs / pure-config / pure-process — nothing functional to verify), transition straight to **Done**. *Amended 2026-06-11 (WX-161): the In Test status sits between merge and Done.*
6. **Stop at Done — do not transition to Closed.** The Done→Closed transition is Paul's deliberate human-review checkpoint. Report back what was done and let Paul press the final button.

### Reopening a Done ticket

**Added 2026-06-06** (raised during WX-134).

If a ticket already at **Done** must be reopened to rework its own scope, transition it **Done → In Review** — never jump it back to To Do or In Progress. Atlassian's cycle/lead-time statistics should record the truth: the work re-entered the reviewing phase, not the queue.

Boundary: a defect discovered in *shipped* work normally gets its **own Bug ticket** (the WX-134 precedent — a race found minutes after WX-127's deploy was filed separately, and WX-127 stayed Done). The reopen rule governs only the case where the same ticket's scope is being reworked; which shape applies is a judgment call made when the defect surfaces.

## 13. Functional testing (In Test)

**Added 2026-06-11** (WX-161; the In Test workflow status and WX-160's post-deployment verification motivated it).

Automated unit/integration tests prove the code correct *in isolation*; only verification against the **deployed** system proves it works with real config, real data, real timing, and real integrations. WX-160 was the worked example — 575 green tests, yet the behavior we actually cared about (the gate suppressing real TAF cycles in production) could only be confirmed by watching live cycles. This is the deployment-side analog of §7a's manual UI test procedures, and it has its own Jira status, **In Test** (In Progress category — a merged-but-unverified ticket is not yet Done). It is also called *post-deployment verification* or a *smoke test*; the log-fingerprint flavor we use is *verification in production*.

### When it applies — and when it doesn't

Functional testing is **opt-in, not mandatory**: the workflow's "Any" transitions let an exempt ticket go straight In Review → Done, so testing is never forced.

- **Warranted** — any change with runtime behavior: logic, gate/threshold changes, prompt changes, schema/migrations, fetch/parse changes, the *semantics* of a config value. → at merge, transition to **In Test**.
- **Exempt** — changes with nothing functional to verify: pure-docs (including this WORKFLOW.md edit), pure-config values, pure-process, pure-tooling that ships in no service binary. → at merge, transition straight to **Done**. Same spirit as the §7b test-run exemption.

When in doubt, test — a wrongly-skipped verification is the costlier mistake.

### The In Test loop

- `In Review → In Test` (**Begin Testing**) — at merge, for a warranted change.
- Run the change's verification (below). **PASS** → `In Test → Done` (**PASS: Send to Done**). **FAIL** → `In Test → Done` (**FAIL: Open bug ticket**): the change shipped its scope, so this ticket still completes (Done → Closed), and the residual defect gets its **own Bug ticket** linked back to it. We **fix forward** — a merged ticket is never reworked in place (there is no In Test → In Progress), which is the same stance §12's reopen rule takes (WX-134 precedent).
- `Done → In Test` (**Return to Test**) — re-verify a ticket already at Done (e.g. after a later redeploy). Distinct from `Done → In Review`, which reopens a ticket to rework *its own* scope (§12).

### Two tiers of verification

1. **Baseline smoke (every deploy):** services came up, heartbeat current, the expected version is running (cross-check `deploy-history.log` against the release), no new ERRORs.
2. **Change-specific verification (the warranted change):** confirm the change's *fingerprint* is present in production and the old behavior is gone. Where scriptable, the script lives **in the repo, beside its procedure** at `docs/test-procedures/WX-NN-verify.sh` — e.g. `WX-160-verify.sh`, which reads the service log and reports `taf-fresh` → 0, the new suppressions, and health. Keeping it in the repo (not `Code/tools`) is what lets it **ride the PR** (below) and stay versioned with the code it checks — the script greps that code's literal log strings, so the two must change together. The boundary: *ticket-/version-coupled verification → repo; generic cross-cutting workflow tooling (`check-ci.sh`, `check-cr.sh`) → `Code/tools`, outside the repo.* One documented carve-out: a *generic helper that exists to serve the in-repo verify scripts* lives beside them, not in `Code/tools` — e.g. `deploy-info.sh`, which returns the deploy boundary from `deploy-history.log` for any `WX-NN-verify.sh` — the latest deploy time of one or more named components (or `all`), and, with `--version V`, the most recent deploy matching that version as `timestamp<TAB>commit` (boundary + identity; see *Choosing the verification boundary* below). It is generic, but it must travel and be reviewed with the verify scripts that call it, so the repo is its home.

### Author the procedure *before* review

Like §7a — and **enforced there**, in the pre-push checklist — the functional test procedure — **and its verification script, if any** — is written **during the ticket, before the PR opens**, and rides the PR. Both live in `docs/test-procedures/`: the procedure at `WX-NN.md` (numbered steps with explicit *Expect:* outcomes), its script beside it at `WX-NN-verify.sh` — so Claude `/code-review` (§7d) and CodeRabbit (§9) review the script too, and it is ready the moment the change deploys. (WX-160 was a one-time retroactive exception — its procedure written, and its script relocated into the repo, after merge because they predated this practice.) Record the procedure in the ticket's custom fields: **Test Proc** = `docs/test-procedures/WX-NN.md`, **Test Result** = `PASS yyyy-mm-dd @ <version>` (or `FAIL …`).

### Choosing the verification boundary

The verification compares production behavior *before* vs *after* the deploy, so the boundary must be the **latest deploy time of the component(s) the change touches** — read from `deploy-history.log` (each line is component / version / commit / OK; a single release deploys its components at staggered times). Too *early* a boundary straddles a mixed-version window — the old binary for part of it — and yields false signals; an unrelated service redeployed in the same batch does not count. A change spanning several services keys off the latest of *their* lines.

A verify script resolves this **version-pinned**, via the shared `deploy-info.sh` helper: it embeds the release `VERSION` it tests and its `COMPONENTS` as constants (both knowable when the script is authored — the version bump ships with the change — so the script is review-complete), and the helper returns the most recent `deploy-history.log` entry matching that version among those components, as `timestamp<TAB>commit` (the boundary plus the deployed commit for an identity line). Pinning on the *version* rather than a captured commit means a later release at another version doesn't move the boundary — the test stays anchored to the deploy that shipped its change even across intervening redeploys — and a same-version redeploy moves to the freshest matching line. If the version isn't in the log yet, the verdict is `WAIT` (not deployed). Health is judged against the **equal-length pre-deploy window**: a verify script fails only on errors/degradations *new* relative to that baseline, so a steady background of unrelated errors (e.g. ongoing reconciler degradations) cancels out instead of failing every change's test. A *new* error above that baseline still **fails immediately** (only the `PASS` verdict is gated on a full active day); but because the cancellation needs a window wide enough to span the background's cadence, an early failure should be confirmed against the dumped error lines — a genuinely new error, not the thin-window's failure to cancel — before it is acted on.

## Schema changes

**Added 2026-05-19** (WX-72).

The database schema is managed by EF Core Migrations, not hand-written SQL DDL. Any change that alters the EF model — adding a column, a table, an index, a constraint — must go through the migration pipeline, not into `DatabaseSetup.cs`.

The procedure for a schema change inside a normal ticket:

1. Edit the entity class and/or `WeatherDataContext.OnModelCreating` to describe the desired model.
2. Restore the local `dotnet-ef` tool if it isn't yet: `dotnet tool restore`.
3. Generate a new migration:

   ```bash
   dotnet ef migrations add <DescriptiveName> --project src/MetarParser.Data/MetarParser.Data.csproj
   ```

   On the Windows developer machine `--msbuildprojectextensionspath 'C:\HarderWare\BuildCache\WxServices\MetarParser.Data\obj'` is also needed because `Directory.Build.props` redirects `obj/` outside the Dropbox tree.

4. Review the generated migration in `src/MetarParser.Data/Migrations/`. EF emits a faithful but sometimes verbose representation of the change — hand-edit it if a more efficient or readable form exists, but never change its *semantic* effect.
5. Commit both the migration file and any model changes together.
6. The migration runs automatically on the next service startup via `DatabaseSetup.EnsureSchemaAsync` → `MigrateAsync`.

Do **not** add new `ExecuteSqlRawAsync` blocks to `DatabaseSetup.cs`. The single use of `ExecuteSqlRawAsync` that remains there is the baselining-marker insert that handles existing pre-WX-72 databases; it is not a precedent for new schema changes.

If a schema change requires data movement that EF's generated `Up`/`Down` cannot express (e.g. backfilling a NOT NULL column with computed values), supplement the generated migration body with custom `migrationBuilder.Sql(...)` calls inside the same migration — keep all the change's effects in one migration so it is atomic.

### Heavy migrations — apply out-of-band

**Added 2026-06-03** (WX-113).

Step 6 above (auto-apply at startup) is fine for fast migrations, but a migration that **rewrites a large table** — a size-of-data change such as widening a column's type, adding a `NOT NULL` column with a default across millions of rows, or re-clustering — can take **longer than the Windows Service Control Manager's 30-second start-timeout**. Because `EnsureSchemaAsync` runs *before* `host.RunAsync()`, SCM never receives its "started" ack, kills the process mid-migration, and the transaction rolls back — for *every* service, repeatedly. (This is exactly what happened deploying WX-113's `GfsGrid` `int`→`bigint` widen — all four services failed to start with SCM event 7000/7009.)

For any migration that rewrites a large table, apply it **out-of-band** rather than relying on startup auto-migrate:

1. **Stop** the WxServices Windows services.
2. Generate the migration SQL (PowerShell):

   ```powershell
   dotnet ef migrations script <PreviousMigration> <NewMigration> --project src/MetarParser.Data/MetarParser.Data.csproj --msbuildprojectextensionspath 'C:\HarderWare\BuildCache\WxServices\MetarParser.Data\obj'
   ```

   Run the output in SSMS against `WeatherData`. The EF script performs the DDL **and** inserts the `__EFMigrationsHistory` row in one transaction, so startup auto-migrate then treats it as already applied.
3. **Start** the services. `EnsureSchemaAsync` sees the migration recorded → no-op → the service starts within the 30 s window.

A uniform, tooling-based replacement for this manual step (a dedicated migrator off the startup path) is tracked in **WX-117**.

## Known friction

### Dropbox-induced git lock failures on push

Dropbox's file watcher occasionally locks `.git/refs/remotes/origin/*.lock` or `.git/config.lock` during push. The push itself almost always reaches GitHub regardless — the failure is purely in updating local git state.

Two symptom families:

- **`error: could not lock config file`** or **`fatal: Unable to write new index file`** — the push proceeded but the upstream-tracking config did not get written. Recovery: `rm` the stale zero-byte lock file under `.git/` and re-run the tracking command (e.g. `git branch --set-upstream-to=origin/<branch>`).
- **`error: update_ref failed for ref 'refs/remotes/origin/<branch>'`** — the push reached GitHub but the local `refs/remotes/origin/<branch>` file did not get written, so `git branch -vv` shows no tracking and `gh pr create` rejects the branch with *"you must first push the current branch to a remote."* Recovery: `git fetch origin <branch>:refs/remotes/origin/<branch> --force` (or simply `git fetch origin`) to repopulate the local tracking ref from the remote, then re-run the failed command.

Neither symptom indicates a problem with git, GitHub, or the commit itself — the commit is on the remote and visible to `git ls-remote`. The only thing the lock blocked is local bookkeeping.
