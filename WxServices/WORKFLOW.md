# WxServices development workflow

Last updated 2026-04-18.

This document is the authoritative workflow for landing a change in WxServices. It is a snapshot of the rules Paul and Claude have agreed on over time; when something here conflicts with an ad-hoc direction, this document wins unless the conflict is flagged and the document updated.

The flow intentionally trades speed for audit: every change maps to a Jira ticket, every release maps to an immutable commit hash, and every line of merged code has been seen by a second pair of eyes (CodeRabbit).

## 1. Branch

Work on a feature branch named `WX-NN-short-description`. The branch name **must** contain the Jira key (`WX-NN`) so the GitHub-for-Jira integration auto-links the branch, commits, and PR to the ticket.

Do **not** commit directly to `master`.

## 2. Transition the Jira ticket to In Progress

**Added 2026-04-18.**

As soon as real work starts on a ticket (branch created, first edit made — whichever comes first), transition the Jira ticket from **To Do** to **In Progress**. If it's already In Progress, skip.

This is cheap board hygiene — a 2-second API call — but it serves two purposes:

- **Honest status for anyone reading the project.** A ticket that's been worked on for an hour but still shows "To Do" is a lie of omission.
- **A cue to keep the human in the loop.** Paul has commented that AI assistants tend to "dumb down the human in the loop" unless the workflow structurally resists that pull. A missed In Progress transition is a small signal that the AI-human pair is drifting into autopilot — catching it early is worth more than the transition itself.

## 3. Commit

Write a full detailed multi-line commit message explaining the *why*, not just the *what*. Include a `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>` trailer so attribution is consistent across the history.

Small unrelated tweaks (typo fixes, log-message cleanups, doc edits) may be bundled alongside the main ticket work rather than split into separate PRs. Use judgment: if the tweak would confuse a bisect or complicate a revert, split it.

## 4. VERSIONS.md

For any change that bumps the version in `Directory.Build.props`, add a new row to `VERSIONS.md` with the **commit hash left as `_pending_`** and a detailed narrative section below the table. Per semantic versioning:

- **PATCH** — bug fixes with no behavior change for clients.
- **MINOR** — new features with backwards-compatible behavior.
- **MAJOR** — large changes; reserved for significant reshaping.

The hash is filled in later (step 9) once the PR is finalized and CodeRabbit is clean.

Pure-tooling / pure-docs PRs (e.g. a `.coderabbit.yaml` config change, a WORKFLOW.md edit with no accompanying runtime change) do not bump the version. VERSIONS.md tracks runtime releases; a PR that produces byte-for-byte identical binaries should not announce a new version to email recipients or log files.

## 5. Code walkthrough before push

**Added 2026-04-18.**

After all code edits are written — but before running tests or pushing — walk Paul through every changed file: **what changed, why, any surprises**. Scale the depth to the change:

- **Typo fix / one-line change**: one-sentence summary.
- **Single-file feature**: a paragraph per concern.
- **Multi-file refactor** (e.g. WX-33's 14 files): a file-by-file breakdown, optionally grouped by concern ("rename chain", "config-key updates", "doc updates").

The walkthrough is the moment Paul re-enters the loop. It serves three purposes:

- **Catches mistakes before CodeRabbit does**, which saves the CodeRabbit review fee on trivially-caught issues.
- **Keeps Paul engaged in the engineering.** Paul is explicitly concerned that AI coding assistants tend to dumb down the human reviewer over time. The walkthrough is a structural guard against that atrophy — Paul reads what ships in his name.
- **Lets Paul object before the branch grows more commits.** Uncommitted changes can be amended freely; "fix" commits after a premature push clutter history.

Wait for Paul to say "LGTM" or call out specific edits to adjust before proceeding to step 6. Do not start the test run until the walkthrough is accepted.

## 6. Tests before PR (two sub-steps, both required)

**Added 2026-04-17.**

### 6a. Ask Paul about test coverage

Before pushing, explicitly ask whether the change warrants:

- **(a) new unit tests**,
- **(b) adjustments to existing tests**, or
- **(c) both**.

"Adjustments to existing" is as important as "new" — a behavior change that silently obsoletes a test assertion is a regression hiding behind a green bar.

Some changes (installer scripts, raw config files, prose-only doc edits) cannot be meaningfully unit-tested. That's fine — say so and move on. The phrase *"when it is possible and makes sense"* governs.

### 6b. Run the full test suite

`dotnet test WxServices.sln` must be green before creating the PR. Pre-existing failures in unrelated modules may remain out of scope, but they must be explicitly identified in the PR body so the reviewer (human or AI) knows they're not new regressions.

### Rationale

CodeRabbit costs money per review. Handing a paid reviewer code with test failures wastes both the fee and Paul's follow-up attention. This guardrail keeps the review budget focused on design and correctness issues the tests can't catch.

## 7. Push and open PR

```bash
git push -u origin WX-NN-short-description
gh pr create --title "WX-NN: short description" --body ...
```

PR title is prefixed with `WX-NN:` for traceability. Body has a `## Summary` section and a `## Test plan` checklist. If any pre-existing test failures are unrelated to this PR, note them explicitly.

**Immediately after opening the PR, transition the Jira ticket from In Progress to In Review.** This is the mirror of the To Do → In Progress transition in §2: the code-writing phase has ended and the reviewing phase has started, and the Jira board should reflect that. Same board-hygiene and autopilot-tripwire reasoning applies. Added 2026-04-18 after Paul caught it missing from the first draft of this workflow — the ticket introducing the In-Progress-transition rule itself landed in a state that needed the very In-Review transition that rule had forgotten to mention.

## 8. CodeRabbit review

CodeRabbit (OpenAI-backed) auto-reviews within ~5 minutes. Address valid findings with follow-up commits on the same branch. Decline bad suggestions with a clear rationale written into a reply comment — don't apply every suggestion reflexively. Example precedent: the `App.UserAgent` suggestion on PR #2 was declined because it would have inverted the dependency layering.

AI-reviewer independence matters here: CodeRabbit is OpenAI, Claude is Anthropic. The two-AI split is deliberate — it means the reviewer is not the same model that wrote the code.

CodeRabbit's behavior is tuned via `.coderabbit.yaml` at the repo root (added in WX-32). The config enables the `assertive` review profile, high-level summaries, Jira-issue linking, effort estimates, and per-path guidance for C#, Python, Markdown, and PowerShell. Tune as needed; changes to the yaml are themselves PRs through this same workflow.

## 9. Hash-fill commit

When CodeRabbit is clean, add a **separate commit** whose sole change is filling in the v1.N.N commit hash in `VERSIONS.md` (replacing `_pending_`). The hash to record is the SHA of the main commit on the feature branch, not the merge commit.

Skip this step for pure-tooling / pure-docs PRs that did not bump the version (see §4).

## 10. Merge — always "Create a merge commit"

- **Never "Squash and merge"** — collapses all branch commits into a new SHA, invalidating the hash recorded in `VERSIONS.md`.
- **Never "Rebase and merge"** — replays commits onto master's HEAD, producing new SHAs.
- **Always "Create a merge commit"** — preserves the original feature-branch SHAs as they appear on master, so every `VERSIONS.md` hash resolves to a real commit.

## 11. Post-merge cleanup

1. Delete the remote branch. GitHub's auto-delete (enabled 2026-04-15) handles this automatically; manual `git push origin --delete WX-NN-...` is only needed if auto-delete is disabled.
2. `git checkout master && git pull` locally.
3. Delete the local branch: `git branch -d WX-NN-...`.
4. Transition the Jira ticket to **Done**.
5. **Stop at Done — do not transition to Closed.** The Done→Closed transition is Paul's deliberate human-review checkpoint. Report back what was done and let Paul press the final button.

## Known friction

Dropbox's file watcher occasionally locks `.git/refs/remotes/origin/*.lock` or `.git/config.lock` during push. Symptoms: `error: could not lock config file` or `fatal: Unable to write new index file`. Push to GitHub usually succeeds regardless; `rm` the stale zero-byte lock file and re-run the tracking command. Not a bug in git or GitHub.
