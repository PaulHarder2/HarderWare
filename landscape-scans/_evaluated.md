# Prior evaluations

Tools and approaches that have been evaluated and either adopted, deferred, or
declined. The monthly landscape scan reads this file (STEP 2 of the routine prompt)
and avoids resurfacing declined items unless their named re-evaluate trigger has
fired.

## Declined

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
