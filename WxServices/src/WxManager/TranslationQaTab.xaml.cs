using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using WxServices.Common;
using WxServices.Common.TranslationQa;

namespace WxManager;

/// <summary>
/// WX-219 — the Translation-QA review tab. Reads judge packages (request.json + judged.json) from the
/// translation-QA folder and presents, per scenario, the English reference / target report /
/// back-translation, the report-level findings, and a joined vocabulary table. Source-agnostic: a
/// package may come from the Gemini judge, a manual paste, or (later) a human reviewer (WX-173).
///
/// SKELETON (Phase 3): selector + trust bar + scenario sub-tabs (report panes stubbed) + the joined
/// vocabulary grid. WebView2 report rendering is Phase 4; copy-to-DB is Phase 5.
/// </summary>
public partial class TranslationQaTab : UserControl
{
    private readonly string _qaFolder;

    public TranslationQaTab()
    {
        InitializeComponent();
        string folder;
        try
        {
            folder = Path.Combine(new WxPaths(WxPaths.ReadInstallRoot()).InstallRoot, "translation-qa");
        }
        catch
        {
            folder = @"C:\HarderWare\translation-qa";
        }
        _qaFolder = folder;

        Loaded += (_, _) => Reload();
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) Reload(); };
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => Reload();

    private void Reload()
    {
        var current = (PackageSelector.SelectedItem as JudgePackageRef)?.JudgedPath;

        IReadOnlyList<JudgePackageRef> refs;
        try
        {
            refs = JudgePackageStore.Discover(_qaFolder);
        }
        catch (Exception ex)
        {
            ShowEmpty($"Could not read {_qaFolder}:\n{ex.Message}");
            return;
        }

        PackageSelector.ItemsSource = refs;
        PackageSelector.DisplayMemberPath = nameof(JudgePackageRef.DisplayName);

        if (refs.Count == 0)
        {
            PackageSelector.SelectedItem = null;
            ShowEmpty($"No judge packages found in {_qaFolder}.\n\nRun the TranslationQa tool (--judge gemini, or --response for a manual paste) to produce one.");
            return;
        }

        // Restore the prior selection if it still exists, else default to the newest (first).
        PackageSelector.SelectedItem = refs.FirstOrDefault(r => r.JudgedPath == current) ?? refs[0];
        // SelectionChanged drives LoadSelected.
    }

    private void PackageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PackageSelector.SelectedItem is JudgePackageRef r)
            LoadSelected(r);
    }

    private void LoadSelected(JudgePackageRef r)
    {
        JudgePackage pkg;
        try
        {
            pkg = JudgePackageStore.Load(r);
        }
        catch (Exception ex)
        {
            ShowEmpty($"Could not load package {r.DisplayName}:\n{ex.Message}");
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        SubTabs.Visibility = Visibility.Visible;

        var req = pkg.Request;
        var judged = pkg.Judged;

        LangText.Text = "Language: " + (string.IsNullOrWhiteSpace(req.TargetDisplayName)
            ? judged.Language
            : $"{req.TargetDisplayName} ({judged.Language})");
        SourceText.Text = "Source: " + (string.IsNullOrWhiteSpace(judged.JudgedBy) ? "unknown" : judged.JudgedBy);
        SetConfidence(judged.SelfReportedConfidence);

        var flagged = pkg.Vocabulary.Count(v => v.Status is VerdictStatus.Warn or VerdictStatus.Wrong);
        var counts = $"Findings: {judged.ReportFindings.Count}   ·   Vocab flagged: {flagged}/{pkg.Vocabulary.Count}";
        if (pkg.OrphanVerdicts.Count > 0)
            counts += $"   ·   Orphan verdicts: {pkg.OrphanVerdicts.Count}";
        CountsText.Text = counts;

        VocabGrid.ItemsSource = pkg.Vocabulary.Select(VocabRowVm.From).ToList();

        // Rebuild the scenario sub-tabs (everything except the static Vocabulary tab), newest selection first.
        for (var i = SubTabs.Items.Count - 1; i >= 0; i--)
            if (SubTabs.Items[i] is TabItem ti && ti != VocabTab)
                SubTabs.Items.RemoveAt(i);

        var insertAt = 0;
        foreach (var s in req.Scenarios)
        {
            var back = judged.BackTranslations.FirstOrDefault(b => string.Equals(b.Scenario, s.Name, StringComparison.OrdinalIgnoreCase));
            var findings = judged.ReportFindings.Where(f => string.Equals(f.Scenario, s.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            SubTabs.Items.Insert(insertAt++, BuildScenarioTab(s, back, findings));
        }
        SubTabs.SelectedIndex = 0;
    }

    private void SetConfidence(JudgeConfidence? confidence)
    {
        if (confidence is null)
        {
            ConfidenceBadge.Visibility = Visibility.Collapsed;
            ConfidenceNote.Text = string.Empty;
            return;
        }

        ConfidenceBadge.Visibility = Visibility.Visible;
        ConfidenceText.Text = "confidence: " + confidence.Level;
        ConfidenceBadge.Background = (confidence.Level?.Trim().ToLowerInvariant()) switch
        {
            "high" => new SolidColorBrush(Color.FromRgb(0x2e, 0x7d, 0x32)),   // green
            "medium" => new SolidColorBrush(Color.FromRgb(0x8a, 0x6d, 0x00)), // amber
            "low" => new SolidColorBrush(Color.FromRgb(0xa0, 0x20, 0x20)),    // red
            _ => new SolidColorBrush(Color.FromRgb(0x3a, 0x3a, 0x3a)),        // unknown/grey
        };
        ConfidenceNote.Text = confidence.Note ?? string.Empty;
    }

    // A stubbed report pane (Phase 4 replaces these two with WebView2). Back-translation + findings are real.
    private static TabItem BuildScenarioTab(RenderedScenario s, BackTranslation? back, IReadOnlyList<ReportFinding> findings)
    {
        var stack = new StackPanel { Margin = new Thickness(12) };

        stack.Children.Add(Label(s.Synopsis, 13, FontWeights.SemiBold, "#c8c8c8", wrap: true));

        stack.Children.Add(Heading("English reference  /  target report"));
        stack.Children.Add(Stub($"[ both reports render side-by-side here in Phase 4 (WebView2) — " +
            $"English {s.EnglishHtml.Length:N0} chars, target {s.TargetHtml.Length:N0} chars ]"));

        stack.Children.Add(Heading("Back-translation → English"));
        stack.Children.Add(back is null
            ? Muted("(no back-translation for this scenario)")
            : Body(back.English));

        stack.Children.Add(Heading($"Report findings ({findings.Count})"));
        if (findings.Count == 0)
        {
            stack.Children.Add(Muted("(none)"));
        }
        else
        {
            foreach (var f in findings)
            {
                var card = new Border
                {
                    Background = (Brush)new BrushConverter().ConvertFromString("#241f14")!, // tan-tinted dark
                    BorderBrush = (Brush)new BrushConverter().ConvertFromString("#e8a020")!,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 0, 0, 6),
                };
                var inner = new StackPanel();
                inner.Children.Add(Label(f.Location, 12, FontWeights.SemiBold, "#e0c070", wrap: true));
                inner.Children.Add(Body(f.Problem));
                inner.Children.Add(Label("Suggested fix: " + f.SuggestedFix, 12, FontWeights.Normal, "#9fb89f", wrap: true));
                card.Child = inner;
                stack.Children.Add(card);
            }
        }

        return new TabItem
        {
            Header = s.Name,
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack },
        };
    }

    private static TextBlock Heading(string text) =>
        Label(text, 13, FontWeights.Bold, "#a0bcd4", wrap: false, top: 12);

    private static TextBlock Body(string text) =>
        Label(text, 13, FontWeights.Normal, "#c8c8c8", wrap: true);

    private static TextBlock Muted(string text) =>
        Label(text, 12, FontWeights.Normal, "#909090", wrap: true);

    private static UIElement Stub(string text)
    {
        return new Border
        {
            Background = (Brush)new BrushConverter().ConvertFromString("#222222")!,
            BorderBrush = (Brush)new BrushConverter().ConvertFromString("#3a3a3a")!,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 4, 0, 0),
            Child = Label(text, 12, FontWeights.Normal, "#909090", wrap: true),
        };
    }

    private static TextBlock Label(string text, double size, FontWeight weight, string fg, bool wrap, double top = 0)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            Foreground = (Brush)new BrushConverter().ConvertFromString(fg)!,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Margin = new Thickness(0, top, 0, 2),
        };
    }

    private void ShowEmpty(string message)
    {
        EmptyState.Text = message;
        EmptyState.Visibility = Visibility.Visible;
        SubTabs.Visibility = Visibility.Collapsed;
    }

    /// <summary>Flat, display-ready row for the vocabulary <see cref="DataGrid"/>.</summary>
    public sealed class VocabRowVm
    {
        public string StatusLabel { get; init; } = "";
        public string Token { get; init; } = "";
        public string EnglishPhrase { get; init; } = "";
        public string TargetPhrase { get; init; } = "";
        public string Comment { get; init; } = "";
        public string Suggestion { get; init; } = "";

        public static VocabRowVm From(VocabularyRow r) => new()
        {
            StatusLabel = StatusText(r.Status) + (r.HasActionableSuggestion ? "  ✎" : ""),
            Token = r.Token,
            EnglishPhrase = r.EnglishPhrase,
            TargetPhrase = r.TargetPhrase,
            Comment = r.Verdict?.Comment ?? "",
            Suggestion = r.Verdict?.Suggestion ?? "",
        };

        private static string StatusText(VerdictStatus s) => s switch
        {
            VerdictStatus.Ok => "OK",
            VerdictStatus.Warn => "Warn",
            VerdictStatus.Wrong => "Wrong",
            VerdictStatus.NotJudged => "not judged",
            VerdictStatus.Unrepresentable => "unrepresentable",
            _ => s.ToString(),
        };
    }
}