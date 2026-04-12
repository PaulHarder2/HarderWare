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

    public ConfigureTab()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadCurrentValuesAsync();
    }

    // ── Load ────────────────────────────────────────────────────────────────

    private async Task LoadCurrentValuesAsync()
    {
        var cfg = App.Configuration;
        if (cfg is null) return;

        TxtInstallRoot.Text      = cfg["InstallRoot"]              ?? WxPaths.DefaultInstallRoot;
        TxtCondaPythonExe.Text   = cfg["WxVis:CondaPythonExe"]     ?? "";
        TxtWgrib2WslPath.Text    = cfg["Gfs:Wgrib2WslPath"]        ?? "/usr/local/bin/wgrib2";

        TxtHomeIcao.Text         = cfg["Fetch:HomeIcao"]           ?? "";
        TxtHomeLatitude.Text     = cfg["Fetch:HomeLatitude"]       ?? "";
        TxtHomeLongitude.Text    = cfg["Fetch:HomeLongitude"]      ?? "";
        TxtBoundingBoxDeg.Text   = cfg["Fetch:BoundingBoxDegrees"] ?? "9";
        TxtRegionSouth.Text      = cfg["Fetch:RegionSouth"]       ?? "";
        TxtRegionNorth.Text      = cfg["Fetch:RegionNorth"]       ?? "";
        TxtRegionWest.Text       = cfg["Fetch:RegionWest"]        ?? "";
        TxtRegionEast.Text       = cfg["Fetch:RegionEast"]        ?? "";

        TxtConnectionString.Text = cfg["ConnectionStrings:WeatherData"]
            ?? @"Server=.\SQLEXPRESS;Database=WeatherData;Trusted_Connection=True;TrustServerCertificate=True;";

        TxtSmtpHost.Text         = cfg["Smtp:Host"]        ?? "smtp.gmail.com";
        TxtSmtpPort.Text         = cfg["Smtp:Port"]        ?? "587";
        TxtClaudeModel.Text      = cfg["Claude:Model"]      ?? "claude-sonnet-4-6";
        TxtMapExtent.Text        = cfg["WxVis:MapExtent"]   ?? "";
        TxtAlertEmail.Text       = cfg["Monitor:AlertEmail"] ?? "";

        // Secrets come from the database, not config files.
        try
        {
            await using var db = new WeatherDataContext(App.DbOptions);
            var gs = await db.GlobalSettings.FirstOrDefaultAsync(x => x.Id == 1);
            if (gs is not null)
            {
                TxtSmtpUsername.Text     = gs.SmtpUsername    ?? "";
                TxtSmtpPassword.Password = gs.SmtpPassword    ?? "";
                TxtSmtpFromAddress.Text  = gs.SmtpFromAddress ?? "";
                TxtClaudeApiKey.Password = gs.ClaudeApiKey    ?? "";
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not load secrets from database: {ex.Message}");
        }
    }

    // ── Save ─��───────────────────────────────────────────────────────────────

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var installRoot = TxtInstallRoot.Text.Trim();
            if (string.IsNullOrEmpty(installRoot))
            {
                SetStatus("Install Root is required.", false);
                return;
            }

            // Non-secret settings → appsettings.local.json
            var root = new JsonObject
            {
                ["InstallRoot"] = installRoot,
                ["ConnectionStrings"] = new JsonObject
                {
                    ["WeatherData"] = TxtConnectionString.Text.Trim(),
                },
                ["Fetch"] = new JsonObject
                {
                    ["HomeIcao"]           = TxtHomeIcao.Text.Trim().ToUpperInvariant(),
                    ["HomeLatitude"]       = ParseDoubleOrNull(TxtHomeLatitude.Text),
                    ["HomeLongitude"]      = ParseDoubleOrNull(TxtHomeLongitude.Text),
                    ["BoundingBoxDegrees"] = ParseDoubleOrNull(TxtBoundingBoxDeg.Text),
                    ["RegionSouth"]        = ParseDoubleOrNull(TxtRegionSouth.Text),
                    ["RegionNorth"]        = ParseDoubleOrNull(TxtRegionNorth.Text),
                    ["RegionWest"]         = ParseDoubleOrNull(TxtRegionWest.Text),
                    ["RegionEast"]         = ParseDoubleOrNull(TxtRegionEast.Text),
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
                ["Gfs"] = new JsonObject
                {
                    ["Wgrib2WslPath"] = TxtWgrib2WslPath.Text.Trim(),
                },
                ["WxVis"] = new JsonObject
                {
                    ["CondaPythonExe"] = TxtCondaPythonExe.Text.Trim(),
                    ["MapExtent"]      = TxtMapExtent.Text.Trim(),
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

            gs.SmtpUsername    = TxtSmtpUsername.Text.Trim();
            gs.SmtpPassword    = TxtSmtpPassword.Password;
            gs.SmtpFromAddress = TxtSmtpFromAddress.Text.Trim();
            gs.ClaudeApiKey    = TxtClaudeApiKey.Password;
            await db.SaveChangesAsync();
            Logger.Info("Secrets saved to GlobalSettings.");

            SetStatus("Configuration saved.", true);
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
                Host        = TxtSmtpHost.Text.Trim(),
                Port        = int.TryParse(TxtSmtpPort.Text, out var p) ? p : 587,
                Username    = TxtSmtpUsername.Text.Trim(),
                Password    = TxtSmtpPassword.Password,
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
                model      = TxtClaudeModel.Text.Trim(),
                max_tokens = 16,
                messages   = new[] { new { role = "user", content = "Reply with OK." } },
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
            true  => Color.FromRgb(0x4C, 0xAF, 0x50),
            false => Color.FromRgb(0xEF, 0x53, 0x50),
            _     => Color.FromRgb(0x90, 0x90, 0x90),
        });
    }

    private static JsonNode? ParseDoubleOrNull(string text)
        => double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? JsonValue.Create(d)
            : null;
}
