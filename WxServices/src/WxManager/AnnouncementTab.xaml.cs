using System.Net.Http;
using System.Windows;
using System.Windows.Controls;

using MetarParser.Data;
using MetarParser.Data.Entities;

using Microsoft.EntityFrameworkCore;

using WxServices.Common;
using WxServices.Logging;

namespace WxManager;

/// <summary>
/// Announcement tab: compose a plain-text announcement and send it as a
/// Claude-formatted HTML email to all configured recipients.
/// </summary>
public partial class AnnouncementTab : UserControl
{
    /// <summary>
    /// True while a send is in flight, so eligibility re-evaluation can't
    /// re-enable the Send button mid-send (WX-134).
    /// </summary>
    private bool _sending;

    /// <summary>Initialises the AnnouncementTab and its child controls.</summary>
    /// <sideeffects>Calls <see cref="InitializeComponent"/>; wires Send-eligibility tracking.</sideeffects>
    public AnnouncementTab()
    {
        InitializeComponent();

        // Dirty-tracking (WX-134): Send enables only when there is announcement
        // text to send, and returns to disabled after a fully successful send
        // (which clears the text).
        AnnouncementBox.TextChanged += (_, _) => UpdateSendEligibility();
    }

    /// <summary>Send is eligible when there is text and no send is in flight (WX-134).</summary>
    private void UpdateSendEligibility()
    {
        SendBtn.IsEnabled = !_sending && !string.IsNullOrWhiteSpace(AnnouncementBox.Text);
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
    /// Loads secrets from <see cref="GlobalSettings"/>, validates
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
        Dictionary<long, string> langNameById;
        try
        {
            using var db = new WeatherDataContext(App.DbOptions);
            gs = await db.GlobalSettings.FindAsync(1);
            recipients = await db.Recipients.OrderBy(r => r.Name).ToListAsync();
            langNameById = await db.Languages.ToDictionaryAsync(l => l.Id, l => l.DisplayName);
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

        var smtpConfig = new SmtpConfig
        {
            Host = App.SmtpConfig.Host,
            Port = App.SmtpConfig.Port,
            Username = gs?.SmtpUsername ?? "",
            Password = gs?.SmtpPassword ?? "",
            FromAddress = gs?.SmtpFromAddress ?? "",
        };
        var claudeApiKey = gs?.ClaudeApiKey ?? "";

        // ── Validate ──────────────────────────────────────────────────────────

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(claudeApiKey)) missing.Add("Claude:ApiKey");
        if (string.IsNullOrWhiteSpace(smtpConfig.Username)) missing.Add("Smtp:Username");
        if (string.IsNullOrWhiteSpace(smtpConfig.Password)) missing.Add("Smtp:Password");
        if (string.IsNullOrWhiteSpace(smtpConfig.FromAddress)) missing.Add("Smtp:FromAddress");

        if (missing.Count > 0)
        {
            ShowMessages(
                $"Missing configuration — use the Configure tab to set these:\n" +
                string.Join(", ", missing));
            return;
        }

        // ── Send ──────────────────────────────────────────────────────────────

        _sending = true;
        UpdateSendEligibility();
        HideMessages();

        using var http = new HttpClient();
        var formatter = new AnnouncementFormatter(http, claudeApiKey, App.ClaudeModel,
                              App.ClaudeMessagesEndpoint, App.ClaudeApiVersion, App.ClaudeMaxTokens);
        var emailer = new SmtpSender(smtpConfig, "WxAnnounce");
        var defaultLang = App.DefaultLanguage;

        // Resolve each recipient's language to its display name via the Languages
        // registry (WX-166); the FK nav isn't loaded, so resolve from the id map.
        var languageGroups = recipients.GroupBy(
            r => r.LanguageId is long lid && langNameById.TryGetValue(lid, out var name) ? name : defaultLang,
            StringComparer.OrdinalIgnoreCase);

        Logger.Info($"Announcement send started: {recipients.Count} recipient(s), {languageGroups.Count()} language group(s).");

        int sent = 0, failed = 0;
        var errors = new List<string>();

        foreach (var group in languageGroups)
        {
            var language = group.Key;
            var groupList = group.ToList();
            SetProgress($"Formatting for {language} ({groupList.Count} recipient(s))...");

            Logger.Info($"Formatting announcement for language '{language}' ({groupList.Count} recipient(s)).");
            string? html;
            try
            {
                html = await formatter.FormatAsync(text, language);
            }
            catch (Exception ex)
            {
                Logger.Error($"Claude formatting failed for language '{language}'.", ex);
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
                    Logger.Error($"Exception sending to {recipient.Name} <{recipient.Email}>.", ex);
                    ok = false;
                    errors.Add($"Send error for {recipient.Name}: {ex.Message}");
                }

                if (ok)
                {
                    Logger.Info($"Sent to {recipient.Name} <{recipient.Email}>.");
                    sent++;
                }
                else
                {
                    Logger.Warn($"Failed to send to {recipient.Name} <{recipient.Email}>.");
                    failed++;
                    if (errors.Count == 0 || !errors[^1].Contains(recipient.Name))
                        errors.Add($"Failed to send to {recipient.Name} <{recipient.Email}>");
                }
            }
        }

        // ── Report results ────────────────────────────────────────────────────

        Logger.Info($"Announcement send complete: {sent} sent, {failed} failed.");

        _sending = false;
        SetProgress("");

        if (errors.Count > 0)
            ShowMessages($"Sent: {sent}  Failed: {failed}\n\n{string.Join("\n", errors)}");
        else
            AnnouncementBox.Text = "";  // clearing the text disables Send via eligibility tracking

        // Failure path keeps the text, so Send re-enables for a retry.
        UpdateSendEligibility();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Shows <paramref name="message"/> in the amber message panel.</summary>
    /// <param name="message">The text to display.</param>
    /// <sideeffects>Sets <see cref="MessagesText"/> and makes <see cref="MessagesBorder"/> visible.</sideeffects>
    private void ShowMessages(string message)
    {
        MessagesText.Text = message;
        MessagesBorder.Visibility = Visibility.Visible;
    }

    /// <summary>Hides the amber message panel.</summary>
    /// <sideeffects>Collapses <see cref="MessagesBorder"/>.</sideeffects>
    private void HideMessages()
    {
        MessagesBorder.Visibility = Visibility.Collapsed;
        MessagesText.Text = "";
    }

    /// <summary>Updates the progress label.</summary>
    /// <param name="text">Status text, or an empty string to clear.</param>
    /// <sideeffects>Sets <see cref="ProgressText"/>.</sideeffects>
    private void SetProgress(string text)
    {
        ProgressText.Text = text;
    }
}