using MetarParser.Data;
using MetarParser.Data.Entities;
using Microsoft.EntityFrameworkCore;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using WxServices.Common;

namespace WxManager;

/// <summary>
/// Announcement tab: compose a plain-text announcement and send it as a
/// Claude-formatted HTML email to all configured recipients.
/// </summary>
public partial class AnnouncementTab : UserControl
{
    /// <summary>Initialises the AnnouncementTab and its child controls.</summary>
    /// <sideeffects>Calls <see cref="InitializeComponent"/>.</sideeffects>
    public AnnouncementTab()
    {
        InitializeComponent();
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    /// <summary>
    /// Clears the announcement text and hides any displayed messages.
    /// </summary>
    /// <param name="sender">The Cancel button.</param>
    /// <param name="e">Routed event arguments (unused).</param>
    /// <sideeffects>Clears <see cref="AnnouncementBox"/> and collapses <see cref="MessagesBorder"/>.</sideeffects>
    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        AnnouncementBox.Text = "";
        HideMessages();
        SetProgress("");
    }

    /// <summary>
    /// Dismisses the error message panel without clearing the announcement text,
    /// allowing the operator to edit and resend.
    /// </summary>
    /// <param name="sender">The Dismiss button inside the Messages panel.</param>
    /// <param name="e">Routed event arguments (unused).</param>
    /// <sideeffects>Collapses <see cref="MessagesBorder"/>.</sideeffects>
    private void DismissBtn_Click(object sender, RoutedEventArgs e)
    {
        HideMessages();
    }

    /// <summary>
    /// Loads secrets from <see cref="GlobalSettings"/> (falling back to
    /// <c>appsettings.local.json</c> values on <see cref="App"/>), validates
    /// configuration, loads all recipients from the database, groups them by language,
    /// asks Claude to format the announcement HTML for each language group, and emails
    /// each recipient. On complete success the announcement text is cleared. Send
    /// failures are reported in the Messages panel so the operator can retry.
    /// </summary>
    /// <param name="sender">The Send button.</param>
    /// <param name="e">Routed event arguments (unused).</param>
    /// <sideeffects>
    /// Reads <see cref="GlobalSettings"/> and recipients from the SQL Server database.
    /// Makes HTTP POST requests to the Anthropic Messages API.
    /// Sends email via SMTP for each recipient.
    /// Updates <see cref="ProgressText"/>, <see cref="MessagesBorder"/>, and <see cref="AnnouncementBox"/>.
    /// </sideeffects>
    private async void SendBtn_Click(object sender, RoutedEventArgs e)
    {
        var text = AnnouncementBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowMessages("Please enter announcement text before sending.");
            return;
        }

        // ── Load GlobalSettings + recipients from DB ───────────────────────────

        GlobalSettings? gs;
        List<Recipient> recipients;
        try
        {
            using var db = new WeatherDataContext(App.DbOptions);
            gs         = await db.GlobalSettings.FindAsync(1);
            recipients = await db.Recipients.OrderBy(r => r.Name).ToListAsync();
        }
        catch (Exception ex)
        {
            ShowMessages($"Failed to load from database: {ex.Message}");
            return;
        }

        if (recipients.Count == 0)
        {
            ShowMessages("No recipients found. Add recipients on the Recipients tab.");
            return;
        }

        // ── Merge secrets: DB row takes priority, config file is fallback ──────

        var smtpConfig = new SmtpConfig
        {
            Host        = App.SmtpConfig.Host,
            Port        = App.SmtpConfig.Port,
            Username    = gs?.SmtpUsername    ?? App.SmtpConfig.Username,
            Password    = gs?.SmtpPassword    ?? App.SmtpConfig.Password,
            FromAddress = gs?.SmtpFromAddress ?? App.SmtpConfig.FromAddress,
        };
        var claudeApiKey = gs?.ClaudeApiKey ?? App.ClaudeApiKey;

        // ── Validate ──────────────────────────────────────────────────────────

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(claudeApiKey))        missing.Add("Claude:ApiKey");
        if (string.IsNullOrWhiteSpace(smtpConfig.Username)) missing.Add("Smtp:Username");
        if (string.IsNullOrWhiteSpace(smtpConfig.Password)) missing.Add("Smtp:Password");
        if (string.IsNullOrWhiteSpace(smtpConfig.FromAddress)) missing.Add("Smtp:FromAddress");

        if (missing.Count > 0)
        {
            ShowMessages(
                $"Missing configuration — populate these in the GlobalSettings database row or appsettings.local.json:\n" +
                string.Join(", ", missing));
            return;
        }

        // ── Send ──────────────────────────────────────────────────────────────

        SendBtn.IsEnabled = false;
        HideMessages();

        using var http  = new HttpClient();
        var formatter   = new AnnouncementFormatter(http, claudeApiKey, App.ClaudeModel);
        var emailer     = new SmtpSender(smtpConfig, "WxAnnounce");
        var defaultLang = App.DefaultLanguage;

        var languageGroups = recipients.GroupBy(
            r => r.Language ?? defaultLang,
            StringComparer.OrdinalIgnoreCase);

        int sent = 0, failed = 0;
        var errors = new List<string>();

        foreach (var group in languageGroups)
        {
            var language  = group.Key;
            var groupList = group.ToList();
            SetProgress($"Formatting for {language} ({groupList.Count} recipient(s))...");

            string? html;
            try
            {
                html = await formatter.FormatAsync(text, language);
            }
            catch (Exception ex)
            {
                errors.Add($"Claude error ({language}): {ex.Message}");
                failed += groupList.Count;
                continue;
            }

            var subject = LanguageHelper.AnnouncementSubject(language);

            foreach (var recipient in groupList)
            {
                SetProgress($"Sending to {recipient.Name}...");

                bool ok;
                try
                {
                    ok = await emailer.SendAsync(
                        recipient.Email, subject, text,
                        htmlBody: html, toName: recipient.Name);
                }
                catch (Exception ex)
                {
                    ok = false;
                    errors.Add($"Send error for {recipient.Name}: {ex.Message}");
                }

                if (ok)
                    sent++;
                else
                {
                    failed++;
                    if (errors.Count == 0 || !errors[^1].Contains(recipient.Name))
                        errors.Add($"Failed to send to {recipient.Name} <{recipient.Email}>");
                }
            }
        }

        // ── Report results ────────────────────────────────────────────────────

        SendBtn.IsEnabled = true;
        SetProgress("");

        if (errors.Count > 0)
            ShowMessages($"Sent: {sent}  Failed: {failed}\n\n{string.Join("\n", errors)}");
        else
            AnnouncementBox.Text = "";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Shows <paramref name="message"/> in the amber message panel.</summary>
    /// <param name="message">The text to display.</param>
    /// <sideeffects>Sets <see cref="MessagesText"/> and makes <see cref="MessagesBorder"/> visible.</sideeffects>
    private void ShowMessages(string message)
    {
        MessagesText.Text         = message;
        MessagesBorder.Visibility = Visibility.Visible;
    }

    /// <summary>Hides the amber message panel.</summary>
    /// <sideeffects>Collapses <see cref="MessagesBorder"/>.</sideeffects>
    private void HideMessages()
    {
        MessagesBorder.Visibility = Visibility.Collapsed;
        MessagesText.Text         = "";
    }

    /// <summary>Updates the progress label.</summary>
    /// <param name="text">Status text, or an empty string to clear.</param>
    /// <sideeffects>Sets <see cref="ProgressText"/>.</sideeffects>
    private void SetProgress(string text)
    {
        ProgressText.Text = text;
    }
}
