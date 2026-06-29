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
    /// Discover packages in <paramref name="folder"/>: every <c>&lt;iso&gt;.&lt;stamp&gt;.judged.json</c>
    /// that has a sibling <c>&lt;iso&gt;.&lt;stamp&gt;.request.json</c>, newest stamp first. Returns empty
    /// if the folder is absent (the consumer shows "no packages" rather than throwing).
    /// </summary>
    public static IReadOnlyList<JudgePackageRef> Discover(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return [];

        var refs = new List<JudgePackageRef>();
        foreach (var judged in Directory.EnumerateFiles(folder, "*" + JudgedSuffix))
        {
            var name = Path.GetFileName(judged);
            var stem = name[..^JudgedSuffix.Length]; // "<iso>.<stamp>"
            var dot = stem.IndexOf('.');
            if (dot <= 0 || dot == stem.Length - 1)
                continue; // not "<iso>.<stamp>"

            var iso = stem[..dot];
            var stamp = stem[(dot + 1)..];
            var requestPath = Path.Combine(folder, stem + RequestSuffix);
            if (!File.Exists(requestPath))
                continue; // a verdict with no request can't be displayed

            refs.Add(new JudgePackageRef(iso, stamp, requestPath, judged));
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