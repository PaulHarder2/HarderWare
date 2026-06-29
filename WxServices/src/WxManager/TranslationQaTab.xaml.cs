using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

using MetarParser.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Web.WebView2.Wpf;

using WxServices.Common;
using WxServices.Common.TranslationQa;

namespace WxManager;

/// <summary>
/// WX-219 — the Translation-QA review tab. Reads judge packages (request.json + judged.json) from the
/// translation-QA folder and presents, per scenario, the English reference / target report /
/// back-translation (the two reports in WebView2), the report-level findings, and a joined vocabulary
/// table with a copy-suggestion-to-DB action. Source-agnostic: a package may come from the Gemini judge,
/// a manual paste, or (later) a human reviewer (WX-173).
/// </summary>
public partial class TranslationQaTab : UserControl
{
    private readonly string _qaFolder;
    private string _currentIso = ""; // the selected package's target language, for copy-to-DB

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

        // Detach the handler while we repopulate so a programmatic re-select doesn't depend on
        // SelectionChanged firing — record value-equality means re-selecting the same package would
        // otherwise be a no-op and Refresh wouldn't re-read a changed judged.json from disk.
        PackageSelector.SelectionChanged -= PackageSelector_SelectionChanged;
        PackageSelector.ItemsSource = refs;
        PackageSelector.DisplayMemberPath = nameof(JudgePackageRef.DisplayName);

        if (refs.Count == 0)
        {
            PackageSelector.SelectedItem = null;
            PackageSelector.SelectionChanged += PackageSelector_SelectionChanged;
            ShowEmpty($"No judge packages found in {_qaFolder}.\n\nRun the TranslationQa tool (--judge gemini, or --response for a manual paste) to produce one.");
            return;
        }

        // Restore the prior selection if it still exists, else default to the newest (first).
        var target = refs.FirstOrDefault(r => r.JudgedPath == current) ?? refs[0];
        PackageSelector.SelectedItem = target;
        PackageSelector.SelectionChanged += PackageSelector_SelectionChanged;
        LoadSelected(target); // always reload from disk (Refresh re-reads the file)
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
        _currentIso = judged.Language;

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

        // Two-stage default sort: rows with a (non-blank) suggestion first, then alphabetical by token —
        // so the actionable rows float to the top while staying alphabetised within each group.
        VocabGrid.ItemsSource = pkg.Vocabulary.Select(VocabRowVm.From)
            .OrderBy(v => string.IsNullOrWhiteSpace(v.Suggestion) ? 1 : 0)
            .ThenBy(v => v.Token, StringComparer.Ordinal)
            .ToList();

        // Rebuild the scenario sub-tabs (everything except the static Vocabulary tab). Dispose the outgoing
        // tabs' WebView2 controls — each hosts a browser process that is not released just by removing the
        // control from the tree, so without this a session of Refreshes / package switches leaks processes.
        for (var i = SubTabs.Items.Count - 1; i >= 0; i--)
            if (SubTabs.Items[i] is TabItem ti && ti != VocabTab)
            {
                if (ti.Tag is List<WebView2> webs)
                    foreach (var w in webs)
                        w.Dispose();
                SubTabs.Items.RemoveAt(i);
            }

        var targetLabel = string.IsNullOrWhiteSpace(req.TargetDisplayName) ? judged.Language : req.TargetDisplayName!;
        var insertAt = 0;
        foreach (var s in req.Scenarios)
        {
            var back = judged.BackTranslations.FirstOrDefault(b => string.Equals(b.Scenario, s.Name, StringComparison.OrdinalIgnoreCase));
            var findings = judged.ReportFindings.Where(f => string.Equals(f.Scenario, s.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            SubTabs.Items.Insert(insertAt++, BuildScenarioTab(s, back, findings, targetLabel));
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
            "high" => B("#2e7d32"),   // green
            "medium" => B("#8a6d00"), // amber
            "low" => B("#a02020"),    // red
            _ => B("#3a3a3a"),        // unknown/grey
        };
        ConfidenceNote.Text = confidence.Note ?? string.Empty;
    }

    // Per scenario: synopsis, the side-by-side triptych (English | target | back-translation), then the
    // report findings below. The two reports render in WebView2; the back-translation is a matching light
    // pane so the three read as a consistent triptych. The tab's Tag carries its WebView2 controls so
    // LoadSelected can dispose them when the tab is replaced.
    private static TabItem BuildScenarioTab(RenderedScenario s, BackTranslation? back, IReadOnlyList<ReportFinding> findings, string targetLabel)
    {
        var webs = new List<WebView2>();

        var root = new Grid { Margin = new Thickness(8) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                  // synopsis
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // triptych (fills)
        // Findings: a definite height bounds the RichTextBox so its scroll engages reliably (a star row
        // doesn't constrain here — the WebView2 airspace siblings disrupt the nested-star height measure).
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(280) });

        var synopsis = Label(s.Synopsis, 13, FontWeights.SemiBold, "#c8c8c8", wrap: true);
        Grid.SetRow(synopsis, 0);
        root.Children.Add(synopsis);

        var triptych = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        for (var i = 0; i < 3; i++)
            triptych.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var en = ReportFrame("English reference", s.EnglishHtml, webs);
        var tgt = ReportFrame($"{targetLabel} — target (under audit)", s.TargetHtml, webs);
        var bt = BackTranslationFrame("Back-translation → English", back);
        Grid.SetColumn(en, 0);
        Grid.SetColumn(tgt, 1);
        Grid.SetColumn(bt, 2);
        triptych.Children.Add(en);
        triptych.Children.Add(tgt);
        triptych.Children.Add(bt);
        Grid.SetRow(triptych, 1);
        root.Children.Add(triptych);

        // Findings: a fixed header over a read-only RichTextBox. RichTextBox gives reliable scrolling
        // (it never clips its content, unlike a bounded ScrollViewer over wrapped text) and makes all the
        // findings text selectable/copyable; the FlowDocument keeps the per-location gold card and renders
        // multiple findings at one location as a bulleted list (automatic hanging indent).
        var findingsRegion = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        findingsRegion.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        findingsRegion.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var findingsHeader = Heading($"Report findings ({findings.Count})");
        Grid.SetRow(findingsHeader, 0);
        findingsRegion.Children.Add(findingsHeader);

        var findingsBox = new RichTextBox(BuildFindingsDocument(findings))
        {
            IsReadOnly = true,
            IsDocumentEnabled = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = B("#c8c8c8"),
            Padding = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(findingsBox, 1);
        findingsRegion.Children.Add(findingsBox);
        Grid.SetRow(findingsRegion, 2);
        root.Children.Add(findingsRegion);

        return new TabItem { Header = s.Name, Content = root, Tag = webs };
    }

    // The findings as a selectable FlowDocument: one gold-accented Section per location; a location with
    // several findings renders as a bulleted List (automatic hanging indent — the problem and its fix
    // align beneath the bullet's text).
    private static FlowDocument BuildFindingsDocument(IReadOnlyList<ReportFinding> findings)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI, Arial, sans-serif"),
            FontSize = 13,
            PagePadding = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = B("#c8c8c8"),
        };

        if (findings.Count == 0)
        {
            doc.Blocks.Add(new Paragraph(new Run("(none)")) { Foreground = B("#909090") });
            return doc;
        }

        foreach (var group in findings.GroupBy(f => f.Location, StringComparer.OrdinalIgnoreCase))
        {
            var items = group.ToList();
            var section = new Section
            {
                BorderBrush = B("#e8a020"),
                BorderThickness = new Thickness(3, 0, 0, 0),
                Background = B("#241f14"), // tan-tinted dark
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 6),
            };
            section.Blocks.Add(new Paragraph(new Run(group.Key))
            {
                Foreground = B("#e0c070"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4),
            });

            if (items.Count == 1)
            {
                section.Blocks.Add(ProblemPara(items[0]));
                section.Blocks.Add(FixPara(items[0]));
            }
            else
            {
                var list = new List
                {
                    MarkerStyle = TextMarkerStyle.Disc,
                    Margin = new Thickness(0),
                    Padding = new Thickness(18, 0, 0, 0),
                };
                foreach (var f in items)
                {
                    var li = new ListItem();
                    li.Blocks.Add(ProblemPara(f));
                    li.Blocks.Add(FixPara(f));
                    list.ListItems.Add(li);
                }
                section.Blocks.Add(list);
            }
            doc.Blocks.Add(section);
        }
        return doc;
    }

    private static Paragraph ProblemPara(ReportFinding f) =>
        new(new Run(f.Problem)) { Margin = new Thickness(0, 0, 0, 2) };

    private static Paragraph FixPara(ReportFinding f) =>
        new(new Run("Suggested fix: " + f.SuggestedFix)) { Foreground = B("#9fb89f"), Margin = new Thickness(0, 0, 0, 4) };

    // A report pane: WebView2 rendering the (inner) report HTML, wrapped in a minimal document shell.
    // WebView2 init is async; on failure (e.g. runtime absent) the pane shows the error rather than a blank.
    // The created control is registered in <paramref name="sink"/> so it can be disposed when the tab is replaced.
    private static UIElement ReportFrame(string header, string innerHtml, ICollection<WebView2> sink)
    {
        var host = new Grid();
        var error = Label("", 12, FontWeights.Normal, "#c8c8c8", wrap: true);
        error.Margin = new Thickness(12);
        error.Visibility = Visibility.Collapsed;
        var web = new WebView2();
        sink.Add(web);
        host.Children.Add(web);
        host.Children.Add(error);

        var html = WrapReportHtml(innerHtml);
        web.Loaded += async (_, _) =>
        {
            try
            {
                await web.EnsureCoreWebView2Async();
                web.NavigateToString(html);
            }
            catch (Exception ex)
            {
                web.Visibility = Visibility.Collapsed;
                error.Text = "Report could not be rendered (WebView2): " + ex.Message;
                error.Visibility = Visibility.Visible;
            }
        };
        return Frame(header, host);
    }

    private static UIElement BackTranslationFrame(string header, BackTranslation? back)
    {
        var text = new TextBlock
        {
            Text = back?.English ?? "(no back-translation for this scenario)",
            TextWrapping = TextWrapping.Wrap,
            Foreground = B("#1a3a5c"),
            FontFamily = new FontFamily("Arial, Helvetica, sans-serif"),
            FontSize = 14,
            Margin = new Thickness(14),
        };
        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = B("#f0f4f8"), // match the email body bg
            Content = text,
        };
        return Frame(header, scroller);
    }

    // A framed pane: a dark header strip above the content, bordered to read as one of the triptych.
    private static Border Frame(string header, UIElement content)
    {
        var dock = new DockPanel();
        var head = new TextBlock
        {
            Text = header,
            Foreground = B("#c8c8c8"),
            Background = B("#1e1e1e"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Padding = new Thickness(8, 4, 8, 4),
        };
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(content);
        return new Border
        {
            BorderBrush = B("#3a3a3a"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 6, 0),
            Child = dock,
        };
    }

    // Wrap the stored inner report HTML in a minimal document so WebView2 renders it as the recipient sees
    // it (the inner HTML is a centered 600px-wide body; this adds the doc shell + the email page background).
    private static string WrapReportHtml(string innerBody) =>
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" +
        "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"></head>" +
        "<body style=\"margin:0;padding:12px;background:#f0f4f8;font-family:Arial,Helvetica,sans-serif;\">" +
        innerBody + "</body></html>";

    private static TextBlock Heading(string text) =>
        Label(text, 13, FontWeights.Bold, "#a0bcd4", wrap: false, top: 12);

    private static TextBlock Label(string text, double size, FontWeight weight, string fg, bool wrap, double top = 0)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            Foreground = B(fg),
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

    // Copy a verdict's suggestion into LanguageTemplates — the human adjudication step (the suggestion is
    // never auto-applied; a person clicks). Guarded by the {n}-placeholder contract: a suggestion that
    // would drop or change a format placeholder is refused, because that corrupts the renderer's template.
    private async void CopyToDb_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not VocabRowVm row)
            return;
        if (string.IsNullOrEmpty(_currentIso) || string.IsNullOrWhiteSpace(row.Suggestion))
            return;

        if (!PlaceholdersMatch(row.EnglishPhrase, row.Suggestion))
        {
            MessageBox.Show(
                "Cannot apply: the suggestion's {n} placeholders don't match the English contract — applying it " +
                $"would break the template's format string.\n\nEnglish: {row.EnglishPhrase}\nSuggestion: {row.Suggestion}",
                "Placeholder mismatch", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            $"Apply this suggestion to LanguageTemplates?\n\nLanguage: {_currentIso}\nToken: {row.Token}\n" +
            $"New phrase: {row.Suggestion}\nReplaces: {row.TargetPhrase}",
            "Copy suggestion to database", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            await using var ctx = new WeatherDataContext(App.DbOptions);
            var tpl = await ctx.LanguageTemplates.Include(t => t.Language)
                .FirstOrDefaultAsync(t => t.Language!.IsoCode == _currentIso && t.Token == row.Token);
            if (tpl is null)
            {
                MessageBox.Show($"No LanguageTemplates row for {_currentIso} / {row.Token}.", "Not found",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            tpl.Phrase = row.Suggestion;
            tpl.ReviewedBy = "WX-219 translation-QA review";
            tpl.ReviewedAtUtc = DateTime.UtcNow;
            await ctx.SaveChangesAsync();

            // Mark applied on the data item (not the recycled DataGrid button visual) so the disabled/
            // "applied" state survives container recycling when the row scrolls out of view and back.
            row.Applied = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Update failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // The {n} placeholder set the suggestion must preserve, defined by the English source format string.
    private static bool PlaceholdersMatch(string english, string suggestion)
    {
        static IEnumerable<string> Placeholders(string s) =>
            Regex.Matches(s, @"\{\d+\}").Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal);
        return Placeholders(english).SequenceEqual(Placeholders(suggestion));
    }

    // Shared, frozen, cached brushes for the fixed dark-theme palette — parsed once per color and reused
    // (frozen brushes are thread-safe and shareable); used on the UI thread only.
    private static readonly Dictionary<string, Brush> _brushes = new(StringComparer.Ordinal);

    private static Brush B(string hex)
    {
        if (!_brushes.TryGetValue(hex, out var brush))
        {
            brush = (Brush)new BrushConverter().ConvertFromString(hex)!;
            brush.Freeze();
            _brushes[hex] = brush;
        }
        return brush;
    }

    /// <summary>Flat, display-ready row for the vocabulary <see cref="DataGrid"/>.</summary>
    public sealed class VocabRowVm : INotifyPropertyChanged
    {
        public string StatusLabel { get; init; } = "";
        public Brush StatusBrush { get; init; } = Brushes.Gray;
        public string Token { get; init; } = "";
        public string EnglishPhrase { get; init; } = "";
        public string TargetPhrase { get; init; } = "";
        public string Comment { get; init; } = "";
        public string Suggestion { get; init; } = "";

        /// <summary>Base eligibility: the verdict carries an actionable suggestion (set once at build).</summary>
        public bool CanCopy { get; init; }

        private bool _applied;

        /// <summary>Set true once the suggestion has been written to the DB. Notifies so the bound button
        /// (which a recycled DataGrid container re-reads from the data item) reflects it after recycling.</summary>
        public bool Applied
        {
            get => _applied;
            set
            {
                if (_applied == value)
                    return;
                _applied = value;
                OnChanged(nameof(Applied));
                OnChanged(nameof(CanApply));
                OnChanged(nameof(ApplyLabel));
            }
        }

        public bool CanApply => CanCopy && !Applied;
        public string ApplyLabel => Applied ? "applied" : "Copy → DB";

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public static VocabRowVm From(VocabularyRow r) => new()
        {
            StatusLabel = StatusText(r.Status) + (r.HasActionableSuggestion ? "  ✎" : ""),
            StatusBrush = StatusBrushFor(r.Status),
            Token = r.Token,
            EnglishPhrase = r.EnglishPhrase,
            TargetPhrase = r.TargetPhrase,
            Comment = r.Verdict?.Comment ?? "",
            Suggestion = r.Verdict?.Suggestion ?? "",
            CanCopy = r.HasActionableSuggestion,
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

        private static Brush StatusBrushFor(VerdictStatus s) => s switch
        {
            VerdictStatus.Ok => B("#4caf50"),
            VerdictStatus.Warn => B("#d4a017"),
            VerdictStatus.Wrong => B("#e05050"),
            VerdictStatus.NotJudged => B("#808080"),
            VerdictStatus.Unrepresentable => B("#c08040"),
            _ => B("#808080"),
        };
    }
}