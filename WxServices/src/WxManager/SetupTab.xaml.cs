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

    private async Task RunChecksAsync()
    {
        RecheckButton.IsEnabled = false;
        StatusText.Text = "Checking...";
        CheckList.Children.Clear();

        var connStr    = App.Configuration?["ConnectionStrings:WeatherData"] ?? "";
        var wgrib2Path = App.Configuration?["Gfs:Wgrib2WslPath"] ?? "/usr/local/bin/wgrib2";
        var pythonExe  = App.Configuration?["WxVis:CondaPythonExe"] ?? "";

        var checks = new (string Label, string Hint, Func<Task<CheckResult>> Check)[]
        {
            ("SQL Server",      "Install SQL Server Express and enable TCP/IP.",
                () => PrerequisiteChecker.CheckSqlServerAsync(connStr)),

            ("Database",        "The database is created automatically on first service startup.",
                () => PrerequisiteChecker.CheckDatabaseAsync(connStr)),

            ("WSL",             "Run 'wsl --install' from an elevated command prompt.",
                () => PrerequisiteChecker.CheckWslAsync()),

            ("wgrib2",          $"Install wgrib2 inside WSL at {wgrib2Path}.",
                () => PrerequisiteChecker.CheckWgrib2Async(wgrib2Path)),

            ("Conda Python",    "Install Miniconda and set WxVis:CondaPythonExe in config.",
                () => Task.FromResult(PrerequisiteChecker.CheckCondaPython(pythonExe))),

            ("wxvis packages",  "Run: conda activate wxvis && pip install -r requirements.txt",
                () => PrerequisiteChecker.CheckWxVisPackagesAsync(pythonExe)),

            ("Docker",          "Install Docker Desktop (optional — needed for Grafana dashboard).",
                () => PrerequisiteChecker.CheckDockerAsync()),
        };

        int passed = 0;
        int total  = checks.Length;

        foreach (var (label, hint, check) in checks)
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
        }

        var allOk = passed == total;
        // Docker is optional — count as pass even if Docker fails.
        var dockerResult = checks[^1];
        var requiredPassed = passed >= total - 1; // all except Docker

        StatusText.Text = requiredPassed
            ? $"All required checks passed ({passed}/{total})."
            : $"{passed}/{total} checks passed.";
        StatusText.Foreground = new SolidColorBrush(requiredPassed ? Color.FromRgb(0x4C, 0xAF, 0x50) : Color.FromRgb(0xEF, 0x53, 0x50));
        RecheckButton.IsEnabled = true;

        if (requiredPassed)
        {
            Logger.Info($"Prerequisites check: {passed}/{total} passed.");
            AllChecksPassed?.Invoke();
        }
        else
        {
            Logger.Warn($"Prerequisites check: {passed}/{total} passed — some required checks failed.");
        }
    }

    private static Border CreateCheckRow(string label, string status, bool? ok)
    {
        var indicator = new TextBlock
        {
            Text = ok switch { true => "\u2714", false => "\u2718", _ => "\u2022" },
            Foreground = new SolidColorBrush(ok switch
            {
                true  => Color.FromRgb(0x4C, 0xAF, 0x50),
                false => Color.FromRgb(0xEF, 0x53, 0x50),
                _     => Color.FromRgb(0x90, 0x90, 0x90),
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

        var indicator   = (TextBlock)stack.Children[0];
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
