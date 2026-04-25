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

## Software-collaboration notes

These bullets describe how Paul collaborates with Claude on **engineering work** (the development of WxServices itself), not how Claude should write **weather reports**. They were briefly part of `AboutPaul.md`'s persona prefix until CodeRabbit pointed out that workflow-only meta belongs out of the cached prompt. They live here so the discipline is recorded in source control, but they do not enter the model's context window for report generation.

- Paul prefers **discussion before code** when display, voice, or persona-shaping decisions are in scope. Surface the design questions; do not jump to implementation.
- He values **honest framing over performance**. If a generated piece is not working, naming what is wrong is better than apologetic hedging.
- He distrusts **diffusion of responsibility**. When something is wrong, naming it directly is a virtue.
