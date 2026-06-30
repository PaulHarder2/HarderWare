namespace MetarParser.Data.Entities;

/// <summary>
/// Lifecycle state of a service-side translation-QA regeneration for one language (WX-235).
/// Stored as the enum name, not its ordinal.
/// </summary>
public enum QaRerunStatus
{
    /// <summary>
    /// The single in-flight state, from the operator's button press until completion. WxManager
    /// writes this immediately so the button locks at once; <see cref="QaRerunRequest.StartedAtUtc"/>
    /// distinguishes still-queued (null) from claimed-by-the-worker (set).
    /// </summary>
    Running,

    /// <summary>The worker produced a fresh judge package; <see cref="QaRerunRequest.ResultStamp"/> names it.</summary>
    Succeeded,

    /// <summary>
    /// The regeneration <em>process</em> failed, so there is no usable package;
    /// <see cref="QaRerunRequest.Error"/> holds the reason. This is NOT a translation-quality verdict.
    /// </summary>
    Failed,
}

/// <summary>
/// One row per language tracking a "Rerun QA" regeneration (WX-235). WxManager's QaRerunCoordinator
/// writes a <see cref="QaRerunStatus.Running"/> row the instant the operator presses the button; the
/// WxReport.Svc QaRerunWorker atomically claims it (sets <see cref="StartedAtUtc"/>), runs the producer
/// pipeline, and writes <see cref="QaRerunStatus.Succeeded"/> + <see cref="ResultStamp"/> or
/// <see cref="QaRerunStatus.Failed"/> + <see cref="Error"/>. The row is transitioned in place rather
/// than appended, so <see cref="IsoCode"/> is unique (one live rerun per language).
/// </summary>
public class QaRerunRequest
{
    /// <summary>Surrogate primary key (bigint identity).</summary>
    public long Id { get; set; }

    /// <summary>ISO 639-1 code of the language being re-judged. Unique — one row per language.</summary>
    public string IsoCode { get; set; } = "";

    /// <summary>Current lifecycle state.</summary>
    public QaRerunStatus Status { get; set; }

    /// <summary>When the operator pressed Rerun QA (the row is created or reset to Running at this instant).</summary>
    public DateTime RequestedAtUtc { get; set; }

    /// <summary>
    /// When the worker claimed the row and began executing; <see langword="null"/> while still queued.
    /// The atomic "Status = Running AND StartedAtUtc IS NULL → set StartedAtUtc" update is the
    /// single-pickup claim, and a non-null value older than the stuck-run timeout marks an interrupted run.
    /// </summary>
    public DateTime? StartedAtUtc { get; set; }

    /// <summary>When the run reached a terminal state (Succeeded / Failed); <see langword="null"/> while in flight.</summary>
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>On success, the <c>{stamp}</c> of the produced package folder (<c>{iso}.{stamp}</c>); otherwise <see langword="null"/>.</summary>
    public string? ResultStamp { get; set; }

    /// <summary>On failure, the process-failure reason (Claude/Gemini error, missing key, render fault); <see langword="null"/> on success.</summary>
    public string? Error { get; set; }

    /// <summary>Optional operator stamp (parallels <see cref="LanguageTemplate.ReviewedBy"/>).</summary>
    public string? RequestedBy { get; set; }
}