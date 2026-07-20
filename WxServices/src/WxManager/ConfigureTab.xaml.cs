using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using MetarParser.Data;
using MetarParser.Data.Configuration;
using MetarParser.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

using WxServices.Common;
using WxServices.Logging;

namespace WxManager;

/// <summary>
/// Settings editor tab.  Since WX-315 this tab writes only to the database and never to a
/// configuration file: operational settings (SMTP host/port, alert email) go to the <c>Config</c>
/// table via <see cref="ConfigStore"/>, where every service reads them through the WX-313
/// provider; secrets (SMTP credentials, Claude API key) go to the <see cref="GlobalSettings"/> row.
/// <para>
/// The remaining fields are read-only.  The foundational location values and the map extent are
/// set by the setup console (WX-314) and change only by re-running it; the connection string is
/// bootstrap-critical — it is how the database is reached in the first place, so it must stay in
/// <c>appsettings.local.json</c> and cannot be edited from an app that needs it to start.
/// </para>
/// </summary>
public partial class ConfigureTab : UserControl
{
    /// <summary>Raised after a successful save so the parent can re-run prerequisite checks.</summary>
    public event Action? ConfigurationSaved;

    /// <summary>
    /// Suppresses dirty-tracking while <see cref="LoadCurrentValuesAsync"/> sets
    /// fields programmatically — only user edits enable Save (WX-134).
    /// </summary>
    private bool _suppressDirty;

    public ConfigureTab()
    {
        InitializeComponent();

        // Dirty-tracking (WX-134): Save enables only on a user edit. Only the editable fields are
        // tracked — the read-only ones (install root, the foundational location values, map extent,
        // connection string, Claude model) cannot be edited, so tracking them could only ever
        // enable Save for a change the tab is unable to persist (WX-315).
        DirtyTracking.Attach(MarkDirty,
            TxtSmtpHost, TxtSmtpPort, TxtSmtpUsername, TxtSmtpPassword, TxtSmtpFromAddress,
            TxtClaudeApiKey, TxtAlertEmail);

        Loaded += async (_, _) => await LoadCurrentValuesAsync();
    }

    /// <summary>Enables Save on a user edit — no-op during programmatic loads (WX-134).</summary>
    private void MarkDirty()
    {
        if (!_suppressDirty)
            SaveButton.IsEnabled = true;
    }

    // ── Load ────────────────────────────────────────────────────────────────

    private async Task LoadCurrentValuesAsync()
    {
        var cfg = App.Configuration;
        if (cfg is null) return;

        // Fetch the secrets BEFORE entering the suppression window — holding
        // the suppress flag across an await would swallow genuine user
        // keystrokes arriving during a cold secrets query, and the trailing
        // force-disable would then clobber a real edit (review finding).
        GlobalSettings? gs = null;
        try
        {
            await using var db = new WeatherDataContext(App.DbOptions);
            gs = await db.GlobalSettings.FirstOrDefaultAsync(x => x.Id == 1);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not load secrets from database: {ex.Message}");
        }

        _suppressDirty = true;
        try
        {
            ApplyLoadedValues(cfg, gs);  // fully synchronous
        }
        finally
        {
            _suppressDirty = false;
        }
        SaveButton.IsEnabled = false;  // clean state — enables on the first edit (WX-134)
    }

    /// <summary>Synchronous field-population body of <see cref="LoadCurrentValuesAsync"/> under the dirty-suppression wrapper.</summary>
    private void ApplyLoadedValues(IConfiguration cfg, GlobalSettings? gs)
    {
        // Display-only (WX-69): show the authoritative resolved InstallRoot (env var →
        // appsettings.shared.json → default), the same value WxPaths hands the rest of the system.
        TxtInstallRoot.Text = WxPaths.ReadInstallRoot();

        TxtHomeIcao.Text = cfg["Fetch:HomeIcao"] ?? "";
        TxtHomeLatitude.Text = cfg["Fetch:HomeLatitude"] ?? "";
        TxtHomeLongitude.Text = cfg["Fetch:HomeLongitude"] ?? "";
        TxtBoundingBoxDeg.Text = cfg["Fetch:BoundingBoxDegrees"] ?? "9";
        TxtRegionSouth.Text = cfg["Fetch:RegionSouth"] ?? "";
        TxtRegionNorth.Text = cfg["Fetch:RegionNorth"] ?? "";
        TxtRegionWest.Text = cfg["Fetch:RegionWest"] ?? "";
        TxtRegionEast.Text = cfg["Fetch:RegionEast"] ?? "";

        TxtConnectionString.Text = cfg["ConnectionStrings:WeatherData"]
            ?? @"Server=.\SQLEXPRESS;Database=WeatherData;Trusted_Connection=True;TrustServerCertificate=True;";

        TxtSmtpHost.Text = cfg["Smtp:Host"] ?? "smtp.gmail.com";
        TxtSmtpPort.Text = cfg["Smtp:Port"] ?? "587";
        TxtClaudeModel.Text = cfg["Claude:Model"] ?? "claude-sonnet-4-6";
        TxtMapExtent.Text = cfg["WxVis:MapExtent"] ?? "";
        TxtAlertEmail.Text = cfg["Monitor:AlertEmail"] ?? "";

        // Secrets come from the database, not config files (fetched by the
        // caller before the suppression window opened).
        if (gs is not null)
        {
            TxtSmtpUsername.Text = gs.SmtpUsername ?? "";
            TxtSmtpPassword.Password = gs.SmtpPassword ?? "";
            TxtSmtpFromAddress.Text = gs.SmtpFromAddress ?? "";
            TxtClaudeApiKey.Password = gs.ClaudeApiKey ?? "";
        }
    }

    // ── Save ─��───────────────────────────────────────────────────────────────

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // This tab no longer writes appsettings.local.json (WX-315). Everything editable here
            // now lives in the database: operational settings in the Config table (read by every
            // service through the WX-313 provider) and secrets in the GlobalSettings row. The
            // connection string stays in the file because it is bootstrap-critical — it is how we
            // reach the database at all — and is now set by the setup script, not from here.
            //
            // The runtime directories are still ensured: they are located by InstallRoot and the
            // rest of the system assumes they exist.
            var paths = new WxPaths(WxPaths.ReadInstallRoot());
            Directory.CreateDirectory(paths.LogsDir);
            Directory.CreateDirectory(paths.PlotsDir);
            Directory.CreateDirectory(paths.TempDir);

            await using var db = new WeatherDataContext(App.DbOptions);

            // Secrets → GlobalSettings row.
            var gs = await db.GlobalSettings.FirstOrDefaultAsync(x => x.Id == 1);
            if (gs is null)
            {
                gs = new GlobalSettings { Id = 1 };
                db.GlobalSettings.Add(gs);
            }

            gs.SmtpUsername = TxtSmtpUsername.Text.Trim();
            gs.SmtpPassword = TxtSmtpPassword.Password;
            gs.SmtpFromAddress = TxtSmtpFromAddress.Text.Trim();
            gs.ClaudeApiKey = TxtClaudeApiKey.Password;

            // Operational settings → Config table, through the shared write path, which refuses
            // bootstrap-critical keys (BootstrapKeys) and duplicates. Its SaveChanges commits the
            // secret edits above in the same transaction, so a refusal persists neither.
            var rows = OperationalConfig.BuildEditableRows(
                TxtSmtpHost.Text, TxtSmtpPort.Text, TxtAlertEmail.Text);
            var result = await ConfigStore.UpsertAsync(db, rows, DateTime.UtcNow);

            Logger.Info(
                $"Configuration saved: Config {result.Inserted} inserted / {result.Updated} updated / " +
                $"{result.Unchanged} unchanged; secrets written to GlobalSettings.");

            // One-time, intentional refresh (WX-315): re-run every configuration provider so the
            // values just written are live in THIS instance instead of waiting for a restart. Same
            // mechanism the four services use after EnsureSchemaAsync. Deliberately NOT SQL change
            // notification — a save from this tab is the only moment WxManager's own configuration
            // changes underneath it, so a targeted reload is the whole requirement.
            (App.Configuration as IConfigurationRoot)?.Reload();

            // Re-read the fields from the refreshed configuration, so what is displayed is what is
            // now in effect rather than what was typed — a wrong value shows up immediately.
            await LoadCurrentValuesAsync();

            SetStatus("Configuration saved.", true);
            SaveButton.IsEnabled = false;  // back to clean until the next edit (WX-134)
            ConfigurationSaved?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to save configuration: {ex.Message}");
            SetStatus($"Save failed: {ex.Message}", false);
        }
    }

    // ── Test buttons ────��────────────────────────────────────────────────────

    private async void TestDbButton_Click(object sender, RoutedEventArgs e)
    {
        SetStatus("Testing database...", null);
        var result = await PrerequisiteChecker.CheckSqlServerAsync(TxtConnectionString.Text.Trim());
        SetStatus(result.Ok ? $"Connected: {result.Message}" : $"Failed: {result.Message}", result.Ok);
    }

    private async void TestSmtpButton_Click(object sender, RoutedEventArgs e)
    {
        var from = TxtSmtpFromAddress.Text.Trim();
        if (string.IsNullOrEmpty(from))
        {
            SetStatus("Enter a From Address first.", false);
            return;
        }

        SetStatus("Sending test email...", null);
        try
        {
            var smtp = new SmtpConfig
            {
                Host = TxtSmtpHost.Text.Trim(),
                Port = int.TryParse(TxtSmtpPort.Text, out var p) ? p : 587,
                Username = TxtSmtpUsername.Text.Trim(),
                Password = TxtSmtpPassword.Password,
                FromAddress = from,
            };
            var sender2 = new SmtpSender(smtp, "WxManager");
            var ok = await sender2.SendAsync(from, "WxManager Test", "SMTP configuration test — if you received this, email is working.");
            SetStatus(ok ? $"Test email sent to {from}." : "SMTP send failed — check credentials.", ok);
        }
        catch (Exception ex)
        {
            SetStatus($"SMTP error: {ex.Message}", false);
        }
    }

    private async void TestClaudeButton_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = TxtClaudeApiKey.Password;
        if (string.IsNullOrEmpty(apiKey))
        {
            SetStatus("Enter an API key first.", false);
            return;
        }

        SetStatus("Testing Claude API...", null);
        try
        {
            using var http = new HttpClient();
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            req.Content = JsonContent.Create(new
            {
                model = TxtClaudeModel.Text.Trim(),
                max_tokens = 16,
                messages = new[] { new { role = "user", content = "Reply with OK." } },
            });

            var resp = await http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
                SetStatus("Claude API connected.", true);
            else
                SetStatus($"Claude API returned {(int)resp.StatusCode}.", false);
        }
        catch (Exception ex)
        {
            SetStatus($"Claude API error: {ex.Message}", false);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private void SetStatus(string text, bool? ok)
    {
        StatusText.Text = text;
        StatusText.Foreground = new SolidColorBrush(ok switch
        {
            true => Color.FromRgb(0x4C, 0xAF, 0x50),
            false => Color.FromRgb(0xEF, 0x53, 0x50),
            _ => Color.FromRgb(0x90, 0x90, 0x90),
        });
    }

}