namespace WxServices.Common;

/// <summary>
/// The reference baseline for the localized-template system. WX-166 used a
/// <c>HasCompleteTemplates</c> gate here to refuse enabling a language whose templates were
/// incomplete; WX-172 inverted that (enabling a language now triggers asynchronous generation
/// rather than requiring pre-existing templates), so the completeness rule moved into the
/// WX-172 generator's fail-closed validation and the <c>BackfillSeededLanguageReady</c> migration.
/// <para>
/// What remains here is <see cref="BaselineCode"/> — the language whose token set defines
/// "complete" — shared by the WX-172 generator (it translates the baseline into each new
/// language), the renderer's per-recipient send gate, and the migration. Kept in this
/// database-free common assembly so every layer agrees on one baseline.
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
}