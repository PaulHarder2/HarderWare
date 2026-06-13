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

    // Immutable snapshot: isoCode -> phrases. Replaced wholesale on reload; never mutated
    // in place, so readers always see a consistent set.
    private sealed record Snapshot(IReadOnlyDictionary<string, LanguagePhrases> ByIso);

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
        if (!Current().ByIso.TryGetValue(isoCode, out var lang))
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

    /// <summary>The blocked (not-representable) tokens for a language, or an empty set if the language is not loaded.</summary>
    public IReadOnlySet<string> BlockedTokens(string isoCode) =>
        Current().ByIso.TryGetValue(isoCode, out var lang)
            ? lang.BlockedTokens
            : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The full representable token→phrase map for a language, or an empty map if not loaded.</summary>
    public IReadOnlyDictionary<string, string> PhrasesFor(string isoCode) =>
        Current().ByIso.TryGetValue(isoCode, out var lang)
            ? lang.Phrases
            : new Dictionary<string, string>(StringComparer.Ordinal);

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

            if (row.Representable)
                p[row.Token] = row.Phrase;   // unique (LanguageId, Token) index => no real collisions
            else
                blocked[iso].Add(row.Token);
            loaded++;
        }

        var byIso = new Dictionary<string, LanguagePhrases>(StringComparer.Ordinal);
        foreach (var iso in phrases.Keys)
            byIso[iso] = new LanguagePhrases { Phrases = phrases[iso], BlockedTokens = blocked[iso] };

        Logger.Info($"LanguageTemplateStore loaded {loaded} template(s) across {byIso.Count} language(s)"
            + (skipped > 0 ? $"; skipped {skipped} unkeyable row(s)" : "") + ".");
        return new Snapshot(byIso);
    }
}