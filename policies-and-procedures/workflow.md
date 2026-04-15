# HarderWare Development Workflow

This document describes the end-to-end workflow for any change to the HarderWare codebase.  It applies to all repositories under the HarderWare umbrella, with WxServices being the primary example.

Established 2026-04-15 alongside the WX-13 / WX-16 work that exercised the workflow for the first time.

---

## 1. Tooling

- **Source control:** Git, hosted on GitHub at `PaulHarder2/HarderWare` (public repo).
- **Ticketing:** Atlassian Jira Cloud at `https://harderware.atlassian.net`, project key **`WX`**.
- **Jira ↔ GitHub bridge:** "GitHub for Jira" Atlassian Marketplace app.  Auto-links any branch, commit, or PR whose name contains a Jira key (e.g. `WX-13`) to the matching ticket.
- **AI code review:** [CodeRabbit](https://www.coderabbit.ai/) — installed against the GitHub repo, runs OpenAI / Gemini models (deliberately not Claude, so the reviewer is independent from the Claude-assisted author).
- **AI authoring:** Claude Code (Anthropic), with the user (Paul) as the primary developer and Claude as a co-author on every commit.

---

## 2. Ticket lifecycle

Every change starts with a Jira ticket.  Even one-line fixes go through a ticket — the discipline is more valuable than the time saved by skipping it.

States used (project workflow):

```
To Do  →  In Progress  →  In Review  →  Done
```

Always transition the ticket as you move through the workflow.  A ticket in the wrong state misleads the Kanban board and breaks the audit trail.

---

## 3. The full workflow (per ticket)

### 3.1 Create the ticket

- File a Jira ticket in the **WX** project.  Choose the issue type (Story / Task / Bug) thoughtfully — Stories for user-facing features, Tasks for internal work, Bugs for defects.
- Write a self-contained description: motivation, scope, implementation sketch, non-goals, and any verification steps.  A reader should not need to scroll a chat log to understand the ticket.
- If the ticket depends on others, add formal Jira issue links (`is blocked by`, `relates to`).

### 3.2 Transition to **In Progress**

Before writing any code, transition the ticket to **In Progress**.  This makes it visible to anyone glancing at the board that work is underway.

### 3.3 Create a feature branch

- Branch name: `WX-NN-short-description` (e.g. `WX-13-Add-Country-and-State-Province-fields-to-WxStations`).
- The Jira key in the branch name is **mandatory** — that's how GitHub-for-Jira auto-links the work.
- Branch off `master`.

```
git checkout master
git pull
git checkout -b WX-NN-short-description
```

### 3.4 Make the changes

- Code, edit, test locally.
- Keep commits focused but don't fragment trivially.
- Bundle small unrelated tweaks alongside ticket work per the small-tweak policy (e.g. a build-system fix discovered while working on a feature can ride along), but call them out explicitly in the commit message and PR description.
- Update `WxServices/DESIGN.md` whenever the change affects architecture, schema, or behavior described there.
- If the change warrants a version bump (per [Semantic Versioning](https://semver.org)):
  - Bump `WxServices/Directory.Build.props` `<Version>`.
  - Add a row to `WxServices/VERSIONS.md` with the new version, **commit hash blank for now**, date, and a one-line summary.
  - Add a detailed release notes section under the table.

### 3.5 Commit

- Multi-line commit message with a clear subject line prefixed by the Jira key, e.g. `WX-13: Add Country and Region fields to WxStations (v1.3.0)`.
- Body explains *why*, not just *what*.  Include design rationale for non-obvious decisions.
- Always include the Claude co-author trailer:
  ```
  Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
  ```
- Use a HEREDOC for multi-line messages so formatting survives.

### 3.6 Push and open a PR

```
git push -u origin WX-NN-short-description
gh pr create --title "WX-NN: …" --body "…"
```

PR title prefixed with the Jira key.  PR body includes a Summary, optional Implementation notes, a Test plan, and (if applicable) a callout for any bundled unrelated tweaks.

### 3.7 Transition to **In Review**

As soon as the PR is open, transition the Jira ticket to **In Review**.

### 3.8 Address review feedback

- CodeRabbit auto-reviews within ~5 minutes of each push.  It posts a top-level summary comment and inline comments.
- Review CodeRabbit's findings critically.  Apply valid fixes; **decline bad suggestions** with a clear rationale (e.g. CodeRabbit suggested `App.UserAgent` from a shared library on WX-13 — this would have inverted the dependency layering, so it was correctly declined).
- The human reviewer (you) has final say.  CodeRabbit is an assistant, not a gate.
- Address feedback with additional commits on the same branch.  Each push triggers a re-review.
- **Do not use CodeRabbit's "Autofix" button.**  It pushes commits authored by the bot, which muddies attribution and bypasses human judgment on which suggestions to accept.

### 3.9 Hash-fill commit (if version was bumped)

Just before merge, add a single-purpose follow-up commit whose **only** change is filling in the commit hash of the main version-bump commit in `VERSIONS.md`:

```
Record vX.Y.Z commit hash in VERSIONS.md
```

This preserves the convention of documenting each release's exact commit, while keeping the main commit message focused on the work itself.

### 3.10 Merge

- **Always use "Create a merge commit"**, never "Squash and merge".
  - Squashing replaces the main commit's SHA with a new one, **invalidating the hash recorded in `VERSIONS.md`**.
  - Merge-commit preserves all original SHAs on master, so the version history stays valid.
  - "Rebase and merge" also preserves SHAs but eliminates the merge commit; either is acceptable, but merge-commit is the project default for clarity.
- After merging, GitHub auto-deletes the head branch (setting "Automatically delete head branches" was enabled 2026-04-15).

### 3.11 Transition to **Done**

Move the Jira ticket to **Done** immediately after merge.

### 3.12 Local cleanup

```
git checkout master
git pull
git branch -d WX-NN-short-description
```

---

## 4. Conventions and policies

### 4.1 Versioning (SemVer)

- **PATCH** (`X.Y.Z+1`): bug fixes, backward-compatible.
- **MINOR** (`X.Y+1.0`): new features, backward-compatible.
- **MAJOR** (`X+1.0.0`): breaking changes.
- Bump the `<Version>` in `WxServices/Directory.Build.props` and add a `VERSIONS.md` row at the same time.

### 4.2 Commit messages

- Detailed, multi-line.
- Subject line ≤ 72 characters, prefixed with the Jira key.
- Body explains rationale, not just mechanics.
- Always include the Claude co-author trailer when Claude assisted.

### 4.3 Direct commits to `master`

Forbidden under this workflow.  Every change goes through a PR.  This is the entire point of moving away from the previous "direct-to-master" approach.

### 4.4 Bundling small unrelated changes

Permitted under the small-tweak policy: if a tiny improvement is discovered in passing, it can ride along with the current ticket's PR rather than spawning its own ticket.  Call it out explicitly in the commit message and PR body.  Do not abuse this — anything more than a few lines should get its own ticket.

### 4.5 Force pushes, history rewrites

Avoided.  No force-pushing the master branch ever.  Force-pushing a feature branch during review is allowed only if necessary to clean up an obvious mistake; otherwise prefer follow-up commits.

### 4.6 Pre-commit hooks, CI

Not yet established.  WX-12 (CI/CD) covers building these out.  Until then, build verification is manual (`dotnet build` on changed projects before commit).

---

## 5. Known friction points

### 5.1 Dropbox lock files

The HarderWare repo lives inside Dropbox.  Dropbox's file watcher occasionally locks `.git/refs/remotes/origin/<branch>.lock` files during push, producing `unable to update local ref` errors.  The push to GitHub succeeds regardless — only the local tracking ref fails to update.

Recovery:

```
rm -f .git/refs/remotes/origin/<branch-name>.lock
git fetch origin
```

This is cosmetic and does not affect the remote.  Long-term fix: move the repo out of Dropbox, or add `.git/` to a Dropbox ignore list.

---

## 6. Tooling references

- Jira project: https://harderware.atlassian.net/jira/software/projects/WX/boards
- GitHub repo: https://github.com/PaulHarder2/HarderWare
- CodeRabbit dashboard: https://app.coderabbit.ai (activates after first PR review)
- Atlassian "GitHub for Jira" app: https://marketplace.atlassian.com/apps/1219592/github-for-jira
