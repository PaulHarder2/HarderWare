# WxServices development workflow

Last updated 2026-04-23.

This document is the authoritative workflow for landing a change in WxServices. It is a snapshot of the rules Paul and Claude have agreed on over time; when something here conflicts with an ad-hoc direction, this document wins unless the conflict is flagged and the document updated.

The flow intentionally trades speed for audit: every change maps to a Jira ticket, every release maps to an immutable commit hash, and every line of merged code has been seen by a second pair of eyes (CodeRabbit).

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
- Child tickets are created **just-in-time**, not at Epic-grooming time. Each child is groomed individually before it is picked up for work. This avoids front-loading grooming sessions for children whose scope isn't yet in focus.

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

Write a full detailed multi-line commit message explaining the *why*, not just the *what*. Include a `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>` trailer so attribution is consistent across the history.

Small unrelated tweaks (typo fixes, log-message cleanups, doc edits) may be bundled alongside the main ticket work rather than split into separate PRs. Use judgment: if the tweak would confuse a bisect or complicate a revert, split it.

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

### 7b. Run the full test suite

`dotnet test WxServices.sln` must report zero failures before creating the PR. There are no pre-existing-failure exceptions: any failure must be resolved in this PR or split out into a blocking ticket first.

**Exemption:** when the change cannot meaningfully affect test results — pure-docs PRs (WORKFLOW.md, DESIGN.md, etc.), pure-config PRs (e.g. `.coderabbit.yaml`), pure-asset PRs — skip the test run and note the exemption in the PR body. Same governing phrase as §7a applies: *"when it is possible and makes sense."* Running `dotnet test` against a diff that touches no executable surface is ceremony, not safety.

### 7c. Confirm acceptance criteria are met

**Added 2026-04-19.**

Before pushing, read the ticket's **Acceptance** section and explicitly ask Paul whether every listed criterion has been satisfied by the change. Do not assume — say the criteria back and get confirmation.

- If a criterion is unmet because of a mistake (missed scope), fix it before pushing.
- If a criterion is deliberately out of scope for this PR (e.g. the PR is one of several sub-PRs that together satisfy an umbrella ticket, or a criterion was re-scoped during the work), call it out explicitly in the PR body so the reviewer is not left guessing.
- If the ticket has no Acceptance section at all, pause and flag that to Paul before pushing — the ticket itself needs amendment first.

Reason: acceptance criteria are the ticket's explicit contract. Tests verify code correctness; acceptance criteria verify *feature* correctness. Both are needed and neither is a substitute for the other. Skipping this step is the same class of mistake as skipping the code walkthrough — it lets the pair drift into autopilot and ships work whose completeness nobody actually checked.

### Rationale

CodeRabbit costs money per review. Handing a paid reviewer code with test failures wastes both the fee and Paul's follow-up attention. This guardrail keeps the review budget focused on design and correctness issues the tests can't catch.

## 8. Push and open PR

```bash
git push -u origin WX-NN-short-description
gh pr create --title "WX-NN: short description" --body ...
```

PR title is prefixed with `WX-NN:` for traceability. Body has a `## Summary` section and a `## Test plan` checklist. If the §7b exemption is used (pure-docs / pure-config / pure-asset PR), note that the test suite was skipped and why.

**Immediately after opening the PR, transition the Jira ticket from In Progress to In Review.** This is the mirror of the To Do → In Progress transition in §3: the code-writing phase has ended and the reviewing phase has started, and the Jira board should reflect that. Same board-hygiene and autopilot-tripwire reasoning applies. Added 2026-04-18 after Paul caught it missing from the first draft of this workflow — the ticket introducing the In-Progress-transition rule itself landed in a state that needed the very In-Review transition that rule had forgotten to mention.

## 9. CodeRabbit review

CodeRabbit (OpenAI-backed) auto-reviews within ~5 minutes. Address valid findings with follow-up commits on the same branch. Decline bad suggestions with a clear rationale written into a reply comment — don't apply every suggestion reflexively. Example precedent: the `App.UserAgent` suggestion on PR #2 was declined because it would have inverted the dependency layering.

AI-reviewer independence matters here: CodeRabbit is OpenAI, Claude is Anthropic. The two-AI split is deliberate — it means the reviewer is not the same model that wrote the code.

CodeRabbit's behavior is tuned via `.coderabbit.yaml` at the repo root (added in WX-32). The config enables the `assertive` review profile, high-level summaries, Jira-issue linking, effort estimates, and per-path guidance for C#, Python, Markdown, and PowerShell. Tune as needed; changes to the yaml are themselves PRs through this same workflow.

## 10. Hash-fill commit

When CodeRabbit is clean, add a **separate commit** whose sole change is filling in the v1.N.N commit hash in `VERSIONS.md` (replacing `_pending_`). The hash to record is the SHA of the commit that introduced the `Directory.Build.props` version bump — equivalently, the feature branch's HEAD at the moment *before* this hash-fill commit is added. It is never the hash-fill commit itself, and never the merge commit.

Skip this step for pure-tooling / pure-docs PRs that did not bump the version (see §5).

## 11. Merge — always "Create a merge commit"

- **Never "Squash and merge"** — collapses all branch commits into a new SHA, invalidating the hash recorded in `VERSIONS.md`.
- **Never "Rebase and merge"** — replays commits onto master's HEAD, producing new SHAs.
- **Always "Create a merge commit"** — preserves the original feature-branch SHAs as they appear on master, so every `VERSIONS.md` hash resolves to a real commit.

## 12. Post-merge cleanup

1. Delete the remote branch. GitHub's auto-delete (enabled 2026-04-15) handles this automatically; manual `git push origin --delete WX-NN-...` is only needed if auto-delete is disabled.
2. `git checkout master && git pull` locally.
3. Delete the local branch: `git branch -d WX-NN-...`.
4. **Log time spent.** Add a Jira worklog entry recording combined pair-effort from grooming through merge (e.g. `45m`, to 15-minute granularity). This closes the calibration loop with the Original estimate set at grooming time. *Added 2026-04-23.*
5. Transition the Jira ticket to **Done**.
6. **Stop at Done — do not transition to Closed.** The Done→Closed transition is Paul's deliberate human-review checkpoint. Report back what was done and let Paul press the final button.

## Known friction

Dropbox's file watcher occasionally locks `.git/refs/remotes/origin/*.lock` or `.git/config.lock` during push. Symptoms: `error: could not lock config file` or `fatal: Unable to write new index file`. Push to GitHub usually succeeds regardless; `rm` the stale zero-byte lock file and re-run the tracking command. Not a bug in git or GitHub.
