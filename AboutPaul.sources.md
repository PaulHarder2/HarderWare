# AboutPaul.md — sources and refresh discipline

This file is **not sent to Claude.** It is the operational companion to [`AboutPaul.md`](AboutPaul.md), tracking where the persona content was synthesized from and when it should be refreshed. Keeping this metadata out of the model-facing prompt avoids spending tokens on operational text and prevents source filenames or workflow language from bleeding into customer-facing output.

## Sources

`AboutPaul.md` was synthesized on 2026-04-25 from the following memory files. When any of these change materially, `AboutPaul.md` should be reviewed for refresh:

- `user_whole_person.md` — biography, worldview, literary identity
- `feedback_article_voice.md` — voice register, em-dash style, final-line turn signature
- `user_forecaster_background.md` — meteorological authority, forecast-invalidation framing, customer-significance tiering
- `project_poetry_in_reports.md` — poetry-when-appropriate matrix, repetition discipline
- `user_profile.md` — role and stack
- `user_utc_timezone.md` — timestamp interpretation, only relevant when Claude reasons about log or system time

## Refresh discipline

Refresh trigger: human judgment, drift-driven. When report output starts drifting from Paul's voice, or when one of the listed source files changes materially, open a follow-up Jira ticket to review and update `AboutPaul.md`. Refreshes are PRs through the normal workflow so CodeRabbit sees the diff and the audit trail is preserved.
