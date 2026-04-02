// WxAnnounce — Service announcement console application.
//
// Usage: WxAnnounce.exe
//
// Workflow:
//   1. Operator writes announcement text to the configured file (default: C:\HarderWare\announce.txt).
//   2. Run WxAnnounce.exe. The app verifies the file is recent (within MaxAgeMinutes),
//      calls Claude to format the text as HTML in each recipient's language, and
//      emails the result to all configured subscribers.
//
// Exit codes:
//   0 — all sends succeeded
//   1 — aborted (configuration invalid, or file missing / too old / empty)
//   3 — one or more sends failed (see log for details)

using Microsoft.Extensions.Configuration;
using System.Windows.Forms;
using WxAnnounce;
using WxServices.Common;
using WxServices.Logging;

Logger.Initialise();

bool testMode = args.Contains("-test", StringComparer.OrdinalIgnoreCase);
Logger.Info(testMode ? "WxAnnounce starting (test mode — first recipient only)." : "WxAnnounce starting.");

// ── Configuration ─────────────────────────────────────────────────────────────

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.shared.json", optional: false)
    .AddJsonFile("appsettings.json",        optional: true)
    .AddJsonFile(@"C:\HarderWare\appsettings.local.json", optional: true)
    .AddJsonFile("appsettings.local.json",  optional: true)
    .Build();

var announceConfig = config.GetSection("Announce").Get<AnnounceConfig>() ?? new AnnounceConfig();
var smtpConfig     = config.GetSection("Smtp").Get<SmtpConfig>()         ?? new SmtpConfig();
var claudeApiKey   = config["Claude:ApiKey"]                              ?? "";
var claudeModel    = config["Claude:Model"]                               ?? "claude-sonnet-4-6";
var defaultLang    = config["Report:DefaultLanguage"]                     ?? "English";

var recipients = config.GetSection("Report:Recipients")
    .Get<List<AnnounceRecipient>>() ?? [];

// ── Validate configuration ────────────────────────────────────────────────────

var issues = new List<string>();
if (string.IsNullOrWhiteSpace(smtpConfig.Username))    issues.Add("Smtp:Username");
if (string.IsNullOrWhiteSpace(smtpConfig.Password))    issues.Add("Smtp:Password");
if (string.IsNullOrWhiteSpace(smtpConfig.FromAddress)) issues.Add("Smtp:FromAddress");
if (string.IsNullOrWhiteSpace(claudeApiKey))           issues.Add("Claude:ApiKey");
if (recipients.Count == 0)                             issues.Add("Report:Recipients (none configured)");

if (issues.Count > 0)
{
    Logger.Error($"Missing required configuration: {string.Join(", ", issues)}. Set these in appsettings.local.json.");
    Console.Error.WriteLine($"ERROR: Missing configuration: {string.Join(", ", issues)}");
    return 1;
}

// ── Check announcement file ───────────────────────────────────────────────────

if (!File.Exists(announceConfig.FilePath))
    return Abort($"Announcement file not found:\n{announceConfig.FilePath}");

var fileAge = DateTime.Now - File.GetLastWriteTime(announceConfig.FilePath);
if (fileAge.TotalMinutes > announceConfig.MaxAgeMinutes)
    return Abort($"Announcement file is {fileAge.TotalMinutes:0} minutes old (max: {announceConfig.MaxAgeMinutes}).\n\nWrite new content and try again.");

var announcementText = (await File.ReadAllTextAsync(announceConfig.FilePath)).Trim();
if (string.IsNullOrWhiteSpace(announcementText))
    return Abort($"Announcement file is empty:\n{announceConfig.FilePath}\n\nWrite your announcement text and try again.");

Logger.Info($"Announcement file accepted: {announceConfig.FilePath} ({announcementText.Length} chars, {fileAge.TotalMinutes:0.0} min old).");

// ── Format and send ───────────────────────────────────────────────────────────

using var http  = new HttpClient();
var formatter   = new AnnouncementFormatter(http, claudeApiKey, claudeModel);
var emailer     = new SmtpSender(smtpConfig, "WxAnnounce");

var sendList = testMode ? recipients.Take(1).ToList() : recipients;
if (testMode) Logger.Info($"Test mode: sending only to {sendList[0].Name} <{sendList[0].Email}>.");

var languageGroups = sendList.GroupBy(
    r => r.Language ?? defaultLang,
    StringComparer.OrdinalIgnoreCase);

int sent = 0, failed = 0;

foreach (var group in languageGroups)
{
    var language  = group.Key;
    var groupList = group.ToList();
    Logger.Info($"Formatting announcement in {language} for {groupList.Count} recipient(s)...");

    var html = await formatter.FormatAsync(announcementText, language);
    if (html is null)
    {
        Logger.Error($"Claude failed to format announcement in {language} — skipping {groupList.Count} recipient(s).");
        failed += groupList.Count;
        continue;
    }

    var subject = BuildSubject(language);

    foreach (var recipient in groupList)
    {
        var ok = await emailer.SendAsync(
            recipient.Email, subject, announcementText,
            htmlBody: html, toName: recipient.Name);

        if (ok) { sent++;   Logger.Info($"  Sent to {recipient.Name} <{recipient.Email}>"); }
        else   { failed++; Logger.Error($"  Failed to send to {recipient.Name} <{recipient.Email}>"); }
    }
}

Logger.Info($"WxAnnounce complete. Sent: {sent}, Failed: {failed}.");
Console.WriteLine($"Done. Sent: {sent}, Failed: {failed}.");

if (failed > 0)
    return Abort($"Announcement sent to {sent} recipient(s), but {failed} failed.\nCheck the log for details:\nC:\\HarderWare\\Logs\\wxannounce.log", exitCode: 3);

return 0;

// ── Helpers ───────────────────────────────────────────────────────────────────

static int Abort(string message, int exitCode = 1)
{
    Logger.Error(message.Replace("\n", " "));
    MessageBox.Show(message, "WxAnnounce", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    return exitCode;
}

static string BuildSubject(string language) => language.ToLowerInvariant() switch
{
    "spanish"    or "español"    => "HarderWare Anuncio de servicio",
    "french"     or "français"   => "HarderWare Annonce de service",
    "german"     or "deutsch"    => "HarderWare Dienstankündigung",
    "portuguese" or "português"  => "HarderWare Anúncio de serviço",
    _                            => "HarderWare Service Announcement",
};
