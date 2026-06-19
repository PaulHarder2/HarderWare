using System.Globalization;

using log4net;

using MetarParser.Data.Entities;

namespace WxReport.Svc;

/// <summary>
/// The in-memory, per-language cache of <see cref="LanguageTemplate"/> rows the
/// deterministic renderer reads its localized phrases from (WX-171). Replaces the
/// hard-coded <see cref="ReportVocabulary"/> string tables as the *source* of phrases:
/// the language-neutral <see cref="LanguageTemplate.Token"/> set stays the code-side
/// contract, while the per-language phrases become data loaded here.
///
/// <para>
/// <b>This type is the load/cache layer only.</b> It maps <c>isoCode → token → phrase</c>;
/// it does not build a <see cref="ReportVocabulary"/> or render anything. Wiring the
/// renderer to consume these atomic tokens is the separate WX-171 rewire — keeping that
/// out of here is what lets the store ship and be tested without disturbing the renderer
/// (the golden no-regression corpus stays green).
/// </para>
///
/// <para>
/// <b>Reload seam (WX-171).</b> Templates are loaded once at construction and held as an
/// immutable snapshot, swapped atomically on <see cref="Reload"/>. <see cref="Invalidate"/>
/// marks the snapshot stale so the next read rebuilds it lazily — the seam for a future
/// "reload when the templates change" trigger (the Mirion ChangeNotification pattern),
/// whose actual change-detection is deliberately deferred to a later ticket.
/// </para>
/// </summary>
public sealed class LanguageTemplateStore
{
    private static readonly ILog Logger = LogManager.GetLogger(typeof(LanguageTemplateStore));

    /// <summary>The per-language phrases for one ISO code: representable token→phrase, plus the blocked tokens.</summary>
    public sealed class LanguagePhrases
    {
        /// <summary>Representable token → localized phrase. Blocked tokens (<see cref="LanguageTemplate.Representable"/> false) are excluded here and listed in <see cref="BlockedTokens"/>.</summary>
        public required IReadOnlyDictionary<string, string> Phrases { get; init; }

        /// <summary>Tokens marked not-representable in this language (WX-172 BLOCKED-needs-code): no usable phrase, so a lookup misses and the caller must fall back.</summary>
        public required IReadOnlySet<string> BlockedTokens { get; init; }
    }

    // Immutable snapshot: isoCode -> phrases, plus isoCode -> IETF culture name (from
    // Language.CultureName). Replaced wholesale on reload; never mutated in place, so
    // readers always see a consistent set.
    private sealed record Snapshot(
        IReadOnlyDictionary<string, LanguagePhrases> ByIso,
        IReadOnlyDictionary<string, string> CultureByIso);

    private readonly Func<IReadOnlyList<LanguageTemplate>> _load;
    private readonly object _gate = new();
    private volatile Snapshot _snapshot;
    private volatile bool _stale;

    /// <summary>
    /// Creates the store and performs the initial load. <paramref name="load"/> returns the
    /// rows to cache (each with its <see cref="LanguageTemplate.Language"/> populated) — in
    /// production a query over <c>WeatherDataContext.LanguageTemplates</c> with the language
    /// included; in tests, a fixed list. It is invoked again on every <see cref="Reload"/>.
    /// </summary>
    public LanguageTemplateStore(Func<IReadOnlyList<LanguageTemplate>> load)
    {
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _snapshot = Build(_load());
    }

    /// <summary>The ISO codes that have at least one loaded template.</summary>
    public IReadOnlySet<string> LoadedLanguages => Current().ByIso.Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Canonicalizes a requested code to the bare lower-case ISO 639-1 part the cache is keyed
    /// on — dropping any regional suffix and case (<c>"es-419"</c> / <c>"ES"</c> → <c>"es"</c>),
    /// so a regional or mixed-case tag resolves to its base language rather than missing the
    /// cache and failing a recipient closed (WX-171; the defense the renderer's former
    /// <c>NormalizeLang</c> provided, kept here as the single iso→templates boundary). This
    /// canonicalizes the lookup KEY only — it never substitutes a different language's phrases.
    /// Public so callers that must agree with the renderer/reconciler on the language key
    /// (e.g. the worker's <c>narrativeLanguages</c>) canonicalize identically (WX-171, review).
    /// </summary>
    public static string CanonicalIso(string isoCode) =>
        string.IsNullOrEmpty(isoCode) ? isoCode : isoCode.Split('-')[0].ToLowerInvariant();

    /// <summary>
    /// Looks up the localized phrase for <paramref name="token"/> in <paramref name="isoCode"/>.
    /// Returns false (and an empty <paramref name="phrase"/>) when the language is not loaded,
    /// the token is absent, or the token is blocked (not representable) in that language — in
    /// every miss the caller is expected to fall back.
    /// </summary>
    public bool TryGetPhrase(string isoCode, string token, out string phrase)
    {
        phrase = "";
        if (string.IsNullOrEmpty(isoCode) || string.IsNullOrEmpty(token))
            return false;
        if (!Current().ByIso.TryGetValue(CanonicalIso(isoCode), out var lang))
            return false;
        // Don't pass `phrase` straight to TryGetValue: on a miss it would overwrite our
        // "" guard with null. Only assign on a hit, so a miss always yields ("", false).
        if (lang.Phrases.TryGetValue(token, out var found))
        {
            phrase = found;
            return true;
        }
        return false;
    }

    /// <summary>The blocked (not-representable) tokens for a language, or an empty set if the language is not loaded. Returns a defensive copy so the shared snapshot's set can't be mutated through the returned reference.</summary>
    public IReadOnlySet<string> BlockedTokens(string isoCode) =>
        Current().ByIso.TryGetValue(CanonicalIso(isoCode), out var lang)
            ? new HashSet<string>(lang.BlockedTokens, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The full representable token→phrase map for a language, or an empty map if not loaded.</summary>
    public IReadOnlyDictionary<string, string> PhrasesFor(string isoCode) =>
        Current().ByIso.TryGetValue(CanonicalIso(isoCode), out var lang)
            ? lang.Phrases
            : new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// A per-language view for the renderer, bound to the current snapshot at resolution
    /// time, so a concurrent <see cref="Reload"/> cannot change phrases mid-render. An
    /// unloaded language yields an empty view (every <see cref="TemplateSet.Get"/> throws);
    /// callers gate on <see cref="MissingTokens"/> first, so they never render an
    /// incomplete language.
    /// </summary>
    public TemplateSet ForLanguage(string isoCode)
    {
        var iso = CanonicalIso(isoCode);
        var phrases = Current().ByIso.TryGetValue(iso, out var lang)
            ? lang.Phrases
            : new Dictionary<string, string>(StringComparer.Ordinal);
        // Carry the normalized iso so a caller keying off TemplateSet.Iso (the renderer's
        // narrative selection) also sees the bare code, not a regional/mixed-case variant.
        return new TemplateSet(iso, phrases);
    }

    /// <summary>
    /// The <see cref="CultureInfo"/> for <paramref name="isoCode"/> — built from the
    /// language's <see cref="Language.CultureName"/> (e.g. <c>"es-US"</c>), used by the
    /// renderer for date/weekday names and number formatting. Falls back to
    /// <c>en-US</c> when the language is not loaded or carries no culture name, and to
    /// <see cref="CultureInfo.InvariantCulture"/> if even that is somehow unresolvable —
    /// a cosmetic locale must never fail a send. (Number conventions stay US/period-decimal
    /// for every language until WX-138 swaps the source to <c>Recipient.NumberFormat</c>.)
    /// </summary>
    public CultureInfo CultureFor(string isoCode)
    {
        var name = !string.IsNullOrEmpty(isoCode) && Current().CultureByIso.TryGetValue(CanonicalIso(isoCode), out var c) && !string.IsNullOrWhiteSpace(c)
            ? c
            : "en-US";
        return SafeCulture(name);
    }

    private static CultureInfo SafeCulture(string name)
    {
        try { return CultureInfo.GetCultureInfo(name); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }

    /// <summary>
    /// The subset of <paramref name="required"/> tokens that do NOT resolve for
    /// <paramref name="isoCode"/> — absent or blocked (not representable). Empty means the
    /// language is complete for that contract. This is the basis of the fail-closed posture
    /// (WX-171): the startup check runs it over <see cref="Tok.All"/> for every supported
    /// language (loud ERROR on any gap), the send path runs it per recipient language, and
    /// WX-172 runs it at enable time. Returns ordinal-sorted for deterministic logging.
    /// </summary>
    public IReadOnlyList<string> MissingTokens(string isoCode, IEnumerable<string> required)
    {
        var has = Current().ByIso.TryGetValue(CanonicalIso(isoCode), out var lang)
            ? lang.Phrases
            : new Dictionary<string, string>(StringComparer.Ordinal);
        return required
            .Where(t => !has.ContainsKey(t))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Eagerly rebuilds the cache from the source and swaps it in atomically. Used at startup and by a future reload trigger.</summary>
    public void Reload()
    {
        lock (_gate)
        {
            _snapshot = Build(_load());
            _stale = false;
        }
    }

    /// <summary>Marks the cache stale so the next read rebuilds it (lazy reload). The seam for a future change-detection trigger; the trigger itself is deferred (WX-171).</summary>
    public void Invalidate() => _stale = true;

    // Returns the current snapshot, rebuilding first if Invalidate() marked it stale. The
    // double-check under the lock keeps a burst of concurrent reads to a single rebuild.
    private Snapshot Current()
    {
        if (!_stale)
            return _snapshot;
        lock (_gate)
        {
            if (_stale)
            {
                _snapshot = Build(_load());
                _stale = false;
            }
            return _snapshot;
        }
    }

    private static Snapshot Build(IReadOnlyList<LanguageTemplate> rows)
    {
        var phrases = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var blocked = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var cultures = new Dictionary<string, string>(StringComparer.Ordinal);
        int loaded = 0, skipped = 0;

        foreach (var row in rows)
        {
            var iso = row.Language?.IsoCode;
            if (string.IsNullOrEmpty(iso) || string.IsNullOrEmpty(row.Token))
            {
                // A row with no language or no token can't be keyed; skip it rather than
                // crash the load (the unique index makes this unexpected, but be defensive).
                skipped++;
                continue;
            }

            if (!phrases.TryGetValue(iso, out var p))
            {
                p = new Dictionary<string, string>(StringComparer.Ordinal);
                phrases[iso] = p;
                blocked[iso] = new HashSet<string>(StringComparer.Ordinal);
            }

            // The culture name lives on the Language, not the row; capture it once per iso
            // (every row of a language carries the same Language, so first-wins is fine).
            if (!cultures.ContainsKey(iso) && !string.IsNullOrWhiteSpace(row.Language?.CultureName))
                cultures[iso] = row.Language!.CultureName!;

            if (row.Representable)
                p[row.Token] = row.Phrase;   // unique (LanguageId, Token) index => no real collisions
            else
                blocked[iso].Add(row.Token);
            loaded++;
        }

        var byIso = new Dictionary<string, LanguagePhrases>(StringComparer.Ordinal);
        foreach (var iso in phrases.Keys)
            // Wrap the per-language map read-only so a caller (PhrasesFor / the TemplateSet view)
            // can't downcast it back to the mutable Dictionary and corrupt the shared snapshot.
            byIso[iso] = new LanguagePhrases { Phrases = phrases[iso].AsReadOnly(), BlockedTokens = blocked[iso] };

        Logger.Info($"LanguageTemplateStore loaded {loaded} template(s) across {byIso.Count} language(s)"
            + (skipped > 0 ? $"; skipped {skipped} unkeyable row(s)" : "") + ".");
        return new Snapshot(byIso, cultures);
    }
}

/// <summary>
/// A per-language phrase view resolved once from a <see cref="LanguageTemplateStore"/>
/// snapshot (see <see cref="LanguageTemplateStore.ForLanguage"/>). Fail-closed by design
/// (WX-171): a missing or blocked token throws <see cref="MissingTemplateException"/>
/// rather than silently substituting English, so an incompleteness that slipped both the
/// build parity gate and the runtime completeness check still fails loudly instead of
/// shipping a half-translated report.
/// </summary>
public sealed class TemplateSet
{
    private readonly IReadOnlyDictionary<string, string> _phrases;

    internal TemplateSet(string iso, IReadOnlyDictionary<string, string> phrases)
    {
        Iso = iso;
        _phrases = phrases;
    }

    /// <summary>The ISO code this view serves.</summary>
    public string Iso { get; }

    /// <summary>
    /// The localized phrase for <paramref name="token"/>. Throws
    /// <see cref="MissingTemplateException"/> if the token is absent or blocked — by design:
    /// completeness is verified before rendering, so a miss here is a defect that must fail
    /// loudly, not degrade silently.
    /// </summary>
    public string Get(string token) =>
        _phrases.TryGetValue(token, out var phrase)
            ? phrase
            : throw new MissingTemplateException(Iso, token);

    /// <summary>True if <paramref name="token"/> has a representable phrase in this language.</summary>
    public bool Has(string token) => _phrases.ContainsKey(token);
}

/// <summary>
/// Thrown when a required template token has no representable phrase for a language at
/// render time. A fail-closed signal: the send path catches it, logs an ERROR (which
/// WxMonitor alerts on), and skips that recipient rather than emitting a broken report.
/// </summary>
public sealed class MissingTemplateException : Exception
{
    public MissingTemplateException(string isoCode, string token)
        : base($"Language template missing for '{isoCode}': token '{token}'.")
    {
        IsoCode = isoCode;
        Token = token;
    }

    public string IsoCode { get; }
    public string Token { get; }
}