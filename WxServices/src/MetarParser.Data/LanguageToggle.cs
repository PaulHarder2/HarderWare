using MetarParser.Data.Entities;

using Microsoft.EntityFrameworkCore;

namespace MetarParser.Data;

/// <summary>
/// The outcome of a <see cref="LanguageToggle.SetEnabledAsync"/> call — enough for the caller
/// (WxManager's Languages tab) to phrase its operator-facing status without re-deriving state.
/// </summary>
public enum LanguageToggleOutcome
{
    /// <summary>The language row was gone (deleted or reloaded out from under the caller).</summary>
    NotFound,

    /// <summary>Enabled and already READY — assignable to recipients now.</summary>
    Enabled,

    /// <summary>Enabled but not yet READY — queued for generation on an upcoming report cycle.</summary>
    EnabledWillGenerate,

    /// <summary>Disabled — its curated templates were kept (WX-249 non-destructive).</summary>
    Disabled,

    /// <summary>Disable refused — recipients are still assigned the language.</summary>
    BlockedByRecipients,
}

/// <summary>The result of a toggle: the <see cref="Outcome"/> plus, when
/// <see cref="LanguageToggleOutcome.BlockedByRecipients"/>, how many recipients blocked it.</summary>
public readonly record struct LanguageToggleResult(LanguageToggleOutcome Outcome, int AssignedRecipients);

/// <summary>
/// WX-249 — the non-destructive enable/disable of a report <see cref="Language"/>, extracted from
/// WxManager's Languages tab so the exact persistence is unit-testable (WxManager is a WPF app,
/// excluded from CI). The invariant it enforces: <b>curated templates are durable data;
/// <see cref="Language.IsEnabled"/> gates USE, not existence.</b>
///
/// Disabling flips <see cref="Language.IsEnabled"/> to false and KEEPS every
/// <see cref="LanguageTemplate"/> row (including the human <c>ReviewedBy</c>/<c>ReviewedAtUtc</c> QA
/// edits) and the <see cref="Language.GeneratedAtUtc"/> stamp — the language becomes dormant
/// (excluded from send/generation by <see cref="Language.IsEnabled"/>, and from the WX-171 startup
/// completeness check). This revises the WX-222 purge-on-disable rule, under which a single disable
/// incinerated all curated review with no undo. Re-enabling reuses the rows in place; WX-250's
/// top-up fills only any baseline tokens added while the language was disabled, so prior review
/// survives untouched.
/// </summary>
public static class LanguageToggle
{
    /// <summary>
    /// Flips <paramref name="languageId"/>'s <see cref="Language.IsEnabled"/> to
    /// <paramref name="enabled"/> and saves, non-destructively. On enable, a stale
    /// <see cref="Language.GenerationError"/> is cleared (defensive recovery of a hand-edited
    /// BLOCKED-while-disabled state) so the language requeues to PENDING. On disable, the recipient
    /// guard is re-checked inside THIS context (the caller's pre-check runs in a separate context
    /// and can race) and no rows are deleted. Returns the <see cref="LanguageToggleResult"/> the
    /// caller maps to a status message; nothing is saved on a NotFound or BlockedByRecipients outcome.
    /// </summary>
    public static async Task<LanguageToggleResult> SetEnabledAsync(
        WeatherDataContext ctx, long languageId, bool enabled, CancellationToken ct = default)
    {
        var row = await ctx.Languages.FindAsync(new object?[] { languageId }, ct);
        if (row is null)
            return new LanguageToggleResult(LanguageToggleOutcome.NotFound, 0);

        if (enabled)
        {
            row.IsEnabled = true;
            // WX-253: recovering a BLOCKED language (block->disable is the WX-253 disable path; this
            // also covers a language hand-edited into BLOCKED while disabled). Requeue to PENDING (clear
            // the stamp + reason) AND delete the generator's own blocked PLACEHOLDER rows — not-
            // representable AND never human-reviewed (empty, Claude-authored) — so those tokens become
            // no-row and the WX-250 auto-scan re-attempts them as fair game once a renderer/code change
            // makes them expressible. A human-REVIEWED row is NEVER deleted (WX-249 durability), even if
            // still marked non-representable: a value an operator typed into a blocked row via the
            // Vocabulary tab keeps its ReviewedBy stamp, so it survives here (it re-blocks the language
            // next cycle rather than being silently destroyed). If a token is still blocked after the
            // re-attempt, block->disable re-fires, so this self-limits: no per-cycle retry, no slot starvation.
            if (row.GenerationError is not null)
            {
                row.GeneratedAtUtc = null;
                row.GenerationError = null;
                var blockedRows = await ctx.LanguageTemplates
                    .Where(t => t.LanguageId == row.Id && !t.Representable && t.ReviewedBy == null)
                    .ToListAsync(ct);
                ctx.LanguageTemplates.RemoveRange(blockedRows);
            }
            await ctx.SaveChangesAsync(ct);
            return new LanguageToggleResult(
                row.IsReady ? LanguageToggleOutcome.Enabled : LanguageToggleOutcome.EnabledWillGenerate, 0);
        }

        // Disabling. Re-check the recipient guard in THIS context so a recipient assigned since the
        // caller's separate pre-check cannot be stranded a language it can no longer render.
        var assigned = await ctx.Recipients.CountAsync(r => r.LanguageId == row.Id, ct);
        if (assigned > 0)
            return new LanguageToggleResult(LanguageToggleOutcome.BlockedByRecipients, assigned);

        // WX-249: NON-DESTRUCTIVE — keep the curated rows and the GeneratedAtUtc stamp; the language
        // is simply dormant. Re-enable reuses the rows; WX-250 tops up only newly-added baseline tokens.
        row.IsEnabled = false;
        await ctx.SaveChangesAsync(ct);
        return new LanguageToggleResult(LanguageToggleOutcome.Disabled, 0);
    }
}