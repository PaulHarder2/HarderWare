namespace WxReport.Svc;

/// <summary>
/// Severity of the change driving a send, used to pick the email's subject-line
/// label and tone.  Since WX-80 the significance <em>judgment</em> lives in
/// Claude (the "is this news?" gate); this enum now only distinguishes how the
/// C# layer labels a send it has already decided to make:
/// <list type="bullet">
///   <item><see cref="None"/> — scheduled or first send; subject "Weather report", no change-summary band.</item>
///   <item><see cref="Update"/> — an arrival-triggered (unscheduled) send; subject "Weather update", change-summary band shown.</item>
///   <item><see cref="Alert"/> — reserved for the WX-81 significance-tier work, where Claude will be able to escalate an unscheduled send to an urgent "Weather alert". WX-80 never emits it; the subject/prompt branches are kept ready.</item>
/// </list>
/// </summary>
public enum ChangeSeverity
{
    /// <summary>Scheduled or first send — no change band, standard "Weather report" subject.</summary>
    None,

    /// <summary>
    /// An unscheduled, arrival-triggered send Claude judged worth sending.
    /// Subject line: "Weather update"; Claude opens with a brief change summary.
    /// </summary>
    Update,

    /// <summary>
    /// Reserved (WX-81): a dangerous change warranting an urgent notice.
    /// Subject line: "Weather alert". Not produced by WX-80.
    /// </summary>
    Alert,
}