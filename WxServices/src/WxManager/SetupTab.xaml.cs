using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using WxServices.Common;
using WxServices.Logging;

namespace WxManager;

/// <summary>
/// Prerequisites checklist tab.  Runs system checks via
/// <see cref="PrerequisiteChecker"/> and displays pass/fail for each.
/// </summary>
public partial class SetupTab : UserControl
{
    /// <summary>
    /// Raised when all prerequisite checks pass, signalling that the
    /// other tabs can be enabled.
    /// </summary>
    public event Action? AllChecksPassed;

    public SetupTab()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RunChecksAsync();
    }

    private async void RecheckButton_Click(object sender, RoutedEventArgs e)
    {
        await RunChecksAsync();
    }

    /// <summary>Triggers a prerequisite re-check programmatically.</summary>
    public async Task RecheckAsync() => await RunChecksAsync();

    private async Task RunChecksAsync()
    {
        RecheckButton.IsEnabled = false;
        StatusText.Text = "Checking...";
        CheckList.Children.Clear();

        var connStr = App.Configuration?["ConnectionStrings:WeatherData"] ?? "";

        // Post-containerization (WX-69): wgrib2, Conda Python, and the wxvis packages moved
        // inside the WxParser/WxVis images (WX-65/66), so they are no longer host prerequisites.
        // Docker is now REQUIRED — the four headless services run in containers, so no Docker
        // means the machine cannot produce reports.  Runtime container HEALTH is deliberately
        // not surfaced here: it is owned by WxMonitor + the autoheal sidecar (WX-68) + Grafana,
        // and per-container `docker inspect` shell-outs would only slow this init.
        // GatesTabs = does failing this check block the other tabs. SQL Server + the database are
        // what WxManager itself needs, so they gate. Docker is required for the *services* to run
        // (shown, and its failure surfaced), but WxManager's own management functions don't need it
        // — so a Docker-down box must not lock you out of the very UI you'd use to fix things.
        var checks = new (string Label, string Hint, bool GatesTabs, Func<Task<CheckResult>> Check)[]
        {
            ("SQL Server",      "Install SQL Server Express and enable TCP/IP.",
                true,  () => PrerequisiteChecker.CheckSqlServerAsync(connStr)),

            ("Database",        "The database is created automatically on first service startup.",
                true,  () => PrerequisiteChecker.CheckDatabaseAsync(connStr)),

            ("Docker",          "Install Docker Desktop and start the stack — the four WxServices run in containers.",
                false, () => PrerequisiteChecker.CheckDockerAsync()),
        };

        int passed = 0;
        int total = checks.Length;
        int gatingTotal = 0;
        int gatingPassed = 0;

        foreach (var (label, hint, gatesTabs, check) in checks)
        {
            var row = CreateCheckRow(label, "Checking...", null);
            CheckList.Children.Add(row);

            CheckResult result;
            try
            {
                result = await check();
            }
            catch (Exception ex)
            {
                result = new CheckResult(false, ex.Message);
            }

            UpdateCheckRow(row, result, hint);
            if (result.Ok) passed++;
            if (gatesTabs)
            {
                gatingTotal++;
                if (result.Ok) gatingPassed++;
            }
        }

        // WX-69: the other tabs enable when the *gating* checks (SQL Server + database) pass — the
        // things WxManager itself needs. Docker is required for the services but must not lock the
        // management UI when it is the only thing down.
        var tabsEnabled = gatingPassed == gatingTotal;
        var allOk = passed == total;

        // green = fully ready; amber = usable but a non-gating check (Docker) is down; red = blocked.
        (string text, Color color) = allOk
            ? ($"All checks passed ({passed}/{total}).", Color.FromRgb(0x4C, 0xAF, 0x50))
            : tabsEnabled
                ? ($"{passed}/{total} passed — start Docker Desktop to run the services.", Color.FromRgb(0xFF, 0xB3, 0x00))
                : ($"{passed}/{total} checks passed.", Color.FromRgb(0xEF, 0x53, 0x50));
        StatusText.Text = text;
        StatusText.Foreground = new SolidColorBrush(color);
        RecheckButton.IsEnabled = true;

        if (tabsEnabled)
        {
            Logger.Info($"Prerequisites check: {passed}/{total} passed (gating {gatingPassed}/{gatingTotal}).");
            AllChecksPassed?.Invoke();
        }
        else
        {
            Logger.Warn($"Prerequisites check: {passed}/{total} passed — a required prerequisite failed.");
        }
    }

    private static Border CreateCheckRow(string label, string status, bool? ok)
    {
        var indicator = new TextBlock
        {
            Text = ok switch { true => "\u2714", false => "\u2718", _ => "\u2022" },
            Foreground = new SolidColorBrush(ok switch
            {
                true => Color.FromRgb(0x4C, 0xAF, 0x50),
                false => Color.FromRgb(0xEF, 0x53, 0x50),
                _ => Color.FromRgb(0x90, 0x90, 0x90),
            }),
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 24,
        };

        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
            VerticalAlignment = VerticalAlignment.Center,
            Width = 130,
        };

        var statusBlock = new TextBlock
        {
            Text = status,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var stack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        stack.Children.Add(indicator);
        stack.Children.Add(labelBlock);
        stack.Children.Add(statusBlock);

        return new Border { Child = stack };
    }

    private static void UpdateCheckRow(Border row, CheckResult result, string hint)
    {
        if (row.Child is not StackPanel stack || stack.Children.Count < 3) return;

        var indicator = (TextBlock)stack.Children[0];
        var statusBlock = (TextBlock)stack.Children[2];

        indicator.Text = result.Ok ? "\u2714" : "\u2718";
        indicator.Foreground = new SolidColorBrush(result.Ok
            ? Color.FromRgb(0x4C, 0xAF, 0x50)
            : Color.FromRgb(0xEF, 0x53, 0x50));

        statusBlock.Text = result.Ok ? result.Message : $"{result.Message}  —  {hint}";
        statusBlock.Foreground = new SolidColorBrush(result.Ok
            ? Color.FromRgb(0x90, 0x90, 0x90)
            : Color.FromRgb(0xEF, 0x93, 0x90));
    }
}