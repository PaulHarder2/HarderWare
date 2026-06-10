namespace WxReport.Svc;

/// <summary>
/// What kind of report a send is — its provenance. The single source of truth
/// (WX-154) for both the email subject word and the rendered header label, so the
/// two cannot drift (the WX-155 defect where a scheduled report carrying a
/// "What's changed" band read as an unscheduled update). Hazard salience — a
/// severe-weather signal surfaced in the subject — is a separate axis added by
/// WX-156, not a report kind.
/// </summary>
public enum ReportKind
{
    /// <summary>A scheduled daily report (or a recipient's first send). Subject "Weather Report"; header "Scheduled Report".</summary>
    Scheduled,

    /// <summary>An arrival-triggered send Claude judged worth sending out of cadence. Subject "Weather Update"; header "Unscheduled Update".</summary>
    Unscheduled,

    /// <summary>A startup/deploy verification send to a single recipient (<see cref="MetarParser.Data.Entities.CommittedSend.IsDiagnostic"/>). Subject and header "Diagnostic".</summary>
    Diagnostic,
}