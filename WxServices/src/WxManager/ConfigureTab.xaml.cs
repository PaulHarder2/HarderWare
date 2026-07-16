using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using MetarParser.Data;
using MetarParser.Data.Entities;

using Microsoft.EntityFrameworkCore;

using WxServices.Common;
using WxServices.Logging;

namespace WxManager;

/// <summary>
/// Settings editor tab.  Non-secret settings are read from configuration
/// files and written to <c>{InstallRoot}\appsettings.local.json</c>.
/// Secrets (SMTP credentials, Claude API key) are stored in the
/// <see cref="GlobalSettings"/> database row and never touch the filesystem.
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

        // Dirty-tracking (WX-134): Save Configuration enables only on a user edit.
        // TxtInstallRoot is display-only (WX-69) — not dirty-tracked; the wgrib2/Conda path
        // fields were removed (those deps now live inside the containers).
        DirtyTracking.Attach(MarkDirty,
            TxtHomeIcao, TxtHomeLatitude, TxtHomeLongitude, TxtBoundingBoxDeg,
            TxtRegionSouth, TxtRegionNorth, TxtRegionWest, TxtRegionEast,
            TxtConnectionString,
            TxtSmtpHost, TxtSmtpPort, TxtSmtpUsername, TxtSmtpPassword, TxtSmtpFromAddress,
            TxtClaudeApiKey, TxtClaudeModel, TxtMapExtent, TxtAlertEmail);

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
    private void ApplyLoadedValues(Microsoft.Extensions.Configuration.IConfiguration cfg, GlobalSettings? gs)
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
            // InstallRoot is resolved (env var → shared.json → default), not set from this tab
            // (WX-69): it is display-only, and appsettings.local.json is *located by* InstallRoot,
            // so it cannot live inside that file. Used here only to place the local config + dirs.
            var installRoot = WxPaths.ReadInstallRoot();

            // Non-secret settings → appsettings.local.json
            var root = new JsonObject
            {
                ["ConnectionStrings"] = new JsonObject
                {
                    ["WeatherData"] = TxtConnectionString.Text.Trim(),
                },
                ["Fetch"] = new JsonObject
                {
                    ["HomeIcao"] = TxtHomeIcao.Text.Trim().ToUpperInvariant(),
                    ["HomeLatitude"] = ParseDoubleOrNull(TxtHomeLatitude.Text),
                    ["HomeLongitude"] = ParseDoubleOrNull(TxtHomeLongitude.Text),
                    ["BoundingBoxDegrees"] = ParseDoubleOrNull(TxtBoundingBoxDeg.Text),
                    ["RegionSouth"] = ParseDoubleOrNull(TxtRegionSouth.Text),
                    ["RegionNorth"] = ParseDoubleOrNull(TxtRegionNorth.Text),
                    ["RegionWest"] = ParseDoubleOrNull(TxtRegionWest.Text),
                    ["RegionEast"] = ParseDoubleOrNull(TxtRegionEast.Text),
                },
                ["Smtp"] = new JsonObject
                {
                    ["Host"] = TxtSmtpHost.Text.Trim(),
                    ["Port"] = int.TryParse(TxtSmtpPort.Text, out var port) ? port : 587,
                },
                ["Claude"] = new JsonObject
                {
                    ["Model"] = TxtClaudeModel.Text.Trim(),
                },
                ["WxVis"] = new JsonObject
                {
                    ["MapExtent"] = TxtMapExtent.Text.Trim(),
                },
                ["Monitor"] = new JsonObject
                {
                    ["AlertEmail"] = TxtAlertEmail.Text.Trim(),
                },
            };

            var paths = new WxPaths(installRoot);
            Directory.CreateDirectory(paths.LogsDir);
            Directory.CreateDirectory(paths.PlotsDir);
            Directory.CreateDirectory(paths.TempDir);

            var localConfigPath = paths.LocalConfigPath;
            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(localConfigPath, json);
            Logger.Info($"Configuration saved to {localConfigPath}.");

            // Secrets → GlobalSettings database row
            await using var db = new WeatherDataContext(App.DbOptions);
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
            await db.SaveChangesAsync();
            Logger.Info("Secrets saved to GlobalSettings.");

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

    private static JsonNode? ParseDoubleOrNull(string text)
        => double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? JsonValue.Create(d)
            : null;
}