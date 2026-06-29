using System.Text.Json;

namespace WxServices.Common.TranslationQa;

/// <summary>
/// The reviewer-facing status of one vocabulary row, derived (WX-219) from the verdict's
/// accurate/natural booleans and the pair's representability — there is no status enum in the raw
/// verdict, so the consumer computes it for at-a-glance coloring.
/// </summary>
public enum VerdictStatus
{
    /// <summary>Generator could not fill this slot (<c>representable: false</c>) — needs code, not a phrase.</summary>
    Unrepresentable,

    /// <summary>A request token the judge returned no verdict for.</summary>
    NotJudged,

    /// <summary>Accurate and natural.</summary>
    Ok,

    /// <summary>Exactly one of accurate / natural is false.</summary>
    Warn,

    /// <summary>Neither accurate nor natural.</summary>
    Wrong,
}

/// <summary>
/// One row of the joined vocabulary view: a request <see cref="VocabularyPair"/> joined with its
/// <see cref="VocabularyVerdict"/> (if the judge returned one), plus the derived <see cref="Status"/>
/// and whether the verdict carries an actionable suggestion (present and different from what we use).
/// </summary>
public sealed record VocabularyRow(
    string Token,
    string EnglishPhrase,
    string EnglishContext,
    string TargetPhrase,
    bool Representable,
    string? Note,
    bool Reviewed,
    VocabularyVerdict? Verdict,
    VerdictStatus Status,
    bool HasActionableSuggestion);

/// <summary>Identity + file paths of a discovered judge package (the paired request + verdict).</summary>
public sealed record JudgePackageRef(string Iso, string Stamp, string RequestPath, string JudgedPath)
{
    /// <summary>Selector label, e.g. "de  ·  20260627-192255".</summary>
    public string DisplayName => $"{Iso}  ·  {Stamp}";
}

/// <summary>A loaded judge package: the request, the verdict, and the joined vocabulary view.</summary>
public sealed record JudgePackage(
    JudgePackageRef Ref,
    JudgingRequest Request,
    JudgeResponse Judged,
    IReadOnlyList<VocabularyRow> Vocabulary,
    IReadOnlyList<VocabularyVerdict> OrphanVerdicts);

/// <summary>
/// WX-219 — discovers and loads judge packages from the translation-QA folder and joins the request
/// vocabulary with the verdict. Pure of WPF so it is unit-testable and reusable (WX-173).
/// </summary>
public static class JudgePackageStore
{
    private const string JudgedSuffix = ".judged.json";
    private const string RequestSuffix = ".request.json";

    /// <summary>
    /// Discover packages in <paramref name="folder"/>: each per-check subfolder named
    /// <c>&lt;iso&gt;.&lt;stamp&gt;</c> that holds both <c>&lt;iso&gt;.&lt;stamp&gt;.judged.json</c> and
    /// <c>&lt;iso&gt;.&lt;stamp&gt;.request.json</c> (WX-232), newest stamp first. Returns empty if the
    /// folder is absent (the consumer shows "no packages" rather than throwing).
    /// </summary>
    public static IReadOnlyList<JudgePackageRef> Discover(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return [];

        var refs = new List<JudgePackageRef>();
        foreach (var sub in Directory.EnumerateDirectories(folder))
        {
            var name = Path.GetFileName(sub); // "<iso>.<stamp>"
            var dot = name.IndexOf('.');
            if (dot <= 0 || dot == name.Length - 1)
                continue; // not "<iso>.<stamp>"

            var judgedPath = Path.Combine(sub, name + JudgedSuffix);
            var requestPath = Path.Combine(sub, name + RequestSuffix);
            if (!File.Exists(judgedPath) || !File.Exists(requestPath))
                continue; // need both the request and the verdict to display a package

            refs.Add(new JudgePackageRef(name[..dot], name[(dot + 1)..], requestPath, judgedPath));
        }

        // Newest first by stamp (yyyyMMdd-HHmmss sorts lexicographically), then iso for stability.
        return [.. refs.OrderByDescending(r => r.Stamp, StringComparer.Ordinal).ThenBy(r => r.Iso, StringComparer.Ordinal)];
    }

    /// <summary>Load + parse + join a package. Throws on a missing/unparseable file (caller reports it).</summary>
    public static JudgePackage Load(JudgePackageRef pkg)
    {
        var request = JsonSerializer.Deserialize<JudgingRequest>(File.ReadAllText(pkg.RequestPath), TranslationQaJson.Read)
            ?? throw new InvalidDataException($"request.json deserialized to null: {pkg.RequestPath}");
        var judged = JsonSerializer.Deserialize<JudgeResponse>(File.ReadAllText(pkg.JudgedPath), TranslationQaJson.Read)
            ?? throw new InvalidDataException($"judged.json deserialized to null: {pkg.JudgedPath}");

        // Coerce omitted JSON arrays to empty (System.Text.Json leaves a missing array null despite the
        // non-nullable annotation) so a hand-edited / out-of-band file with a dropped section still loads
        // instead of NRE-ing the whole otherwise-valid package.
        request = request with { Scenarios = request.Scenarios ?? [], Vocabulary = request.Vocabulary ?? [] };
        judged = judged with
        {
            BackTranslations = judged.BackTranslations ?? [],
            ReportFindings = judged.ReportFindings ?? [],
            VocabularyVerdicts = judged.VocabularyVerdicts ?? [],
        };

        // Enforce the required-field contract that System.Text.Json's nullability-blind binding can't: a
        // hand-edited package with a blank language or a token-less entry should fail fast here, naming the
        // file/field, rather than NRE opaquely inside JoinVocabulary (Token is the join key).
        if (string.IsNullOrWhiteSpace(judged.Language))
            throw new InvalidDataException($"judged.json is missing required 'language': {pkg.JudgedPath}");
        if (request.Vocabulary.Any(v => string.IsNullOrEmpty(v.Token)))
            throw new InvalidDataException($"request.json has a vocabulary entry with no 'token': {pkg.RequestPath}");
        if (judged.VocabularyVerdicts.Any(v => string.IsNullOrEmpty(v.Token)))
            throw new InvalidDataException($"judged.json has a vocabulary verdict with no 'token': {pkg.JudgedPath}");

        var (rows, orphans) = JoinVocabulary(request, judged);
        return new JudgePackage(pkg, request, judged, rows, orphans);
    }

    /// <summary>
    /// Join the request vocabulary (the authoritative token set, kept in order) with the verdicts on
    /// <c>Token</c>. Every request token yields a row (a missing verdict → <see cref="VerdictStatus.NotJudged"/>);
    /// verdicts with no matching request token are returned separately as orphans (surfaced, never silently
    /// dropped). Exposed for unit testing.
    /// </summary>
    public static (IReadOnlyList<VocabularyRow> rows, IReadOnlyList<VocabularyVerdict> orphans)
        JoinVocabulary(JudgingRequest request, JudgeResponse judged)
    {
        var vocab = request.Vocabulary ?? [];
        var verdicts = judged.VocabularyVerdicts ?? [];

        var verdictByToken = new Dictionary<string, VocabularyVerdict>(StringComparer.Ordinal);
        foreach (var v in verdicts)
            verdictByToken[v.Token] = v; // last wins on the rare duplicate token

        var rows = new List<VocabularyRow>(vocab.Count);
        var matched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in vocab)
        {
            verdictByToken.TryGetValue(pair.Token, out var verdict);
            if (verdict is not null)
                matched.Add(pair.Token);

            var status = DeriveStatus(pair, verdict);
            // Same emptiness test the copy-to-DB click guard uses (IsNullOrWhiteSpace), so a whitespace-only
            // suggestion never enables a button whose handler would then no-op.
            var suggestion = verdict?.Suggestion;
            var actionable = !string.IsNullOrWhiteSpace(suggestion)
                && !string.Equals(suggestion, pair.TargetPhrase, StringComparison.Ordinal);

            rows.Add(new VocabularyRow(
                pair.Token, pair.EnglishPhrase, pair.EnglishContext, pair.TargetPhrase,
                pair.Representable, pair.Note, pair.Reviewed, verdict, status, actionable));
        }

        var orphans = verdicts.Where(v => !matched.Contains(v.Token)).ToList();
        return (rows, orphans);
    }

    private static VerdictStatus DeriveStatus(VocabularyPair pair, VocabularyVerdict? verdict)
    {
        if (!pair.Representable)
            return VerdictStatus.Unrepresentable;
        if (verdict is null)
            return VerdictStatus.NotJudged;
        return (verdict.Accurate, verdict.Natural) switch
        {
            (true, true) => VerdictStatus.Ok,
            (false, false) => VerdictStatus.Wrong,
            _ => VerdictStatus.Warn,
        };
    }
}