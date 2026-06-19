namespace WxServices.Common;

/// <summary>
/// The gate WX-166 enforces when enabling a language in WxManager, so a recipient is never
/// assigned a language the renderer cannot actually produce: a language is supported only
/// when its localized report templates cover the full required token set.
/// <para>
/// WX-171 makes this DB-backed (replacing the former hard-coded <c>{en, es}</c> constant):
/// the required set is the tokens the <see cref="BaselineCode"/> language carries — which the
/// build-time <c>Tok</c>↔seed parity gate keeps equal to the renderer's whole token contract —
/// so "supported" means "has every token the baseline has." The token sets are queried from
/// <c>LanguageTemplates</c> by the caller (WxManager, which already has DB access), keeping this
/// assembly free of a database dependency; this type is the pure set-comparison rule the
/// renderer's completeness check and the enable gate now share as one source of truth.
/// </para>
/// </summary>
public static class SupportedLanguages
{
    /// <summary>
    /// The reference language whose stored token set defines "complete" (WX-171). English is
    /// the renderer's authoritative contract — the build-time parity gate asserts its seed
    /// equals the full <c>Tok</c> token set — so a language carrying every <see cref="BaselineCode"/>
    /// token carries every renderer-required token.
    /// </summary>
    public const string BaselineCode = "en";

    /// <summary>
    /// Whether <paramref name="isoTokens"/> (a language's stored, representable template tokens)
    /// covers the full required set <paramref name="baselineTokens"/> (the <see cref="BaselineCode"/>
    /// language's tokens) — so the language may be enabled (moved into the SupportedLanguages set).
    /// False when the baseline itself is empty (nothing to compare against) or the language is
    /// missing any baseline token. Both sets use ordinal comparison; registry codes/tokens are
    /// stored case-exact.
    /// </summary>
    public static bool HasCompleteTemplates(IReadOnlySet<string> isoTokens, IReadOnlySet<string> baselineTokens) =>
        baselineTokens.Count > 0 && baselineTokens.IsSubsetOf(isoTokens);
}