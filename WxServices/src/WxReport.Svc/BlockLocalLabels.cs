using System.Globalization;
using System.Text;

using MetarParser.Data.Entities;

namespace WxReport.Svc;

/// <summary>
/// WX-504: builds the per-report <c>block_local_labels</c> block injected into the reconciler's user
/// message. It gives every forecast block its LOCAL date, weekday, day-part and clock span, keyed by
/// the block's <c>startUtc</c>, so the narrative names a block's time by copying a label instead of
/// converting a UTC instant itself. The WX-340 production watch found that conversion failing in
/// shipped prose — an early-hours block called "morning", a window shifted by a whole day, a
/// midnight-crossing window named by its tail — while the prompt rules against all three were in force.
///
/// <para>
/// Labels come from the local wall clock, never from <c>StartUtc + 6h</c>. Blocks are aligned to local
/// day-part boundaries (WX-155, <c>GfsSnapshotBuilder.FloorToLocalDayPartStart</c>), so across a DST
/// transition a block spans 5 or 7 UTC hours while its local span is still, e.g., 00:00-06:00.
/// </para>
///
/// <para>
/// The day-part names here are English and canonical, like <see cref="DayNameReference"/>'s invariant
/// date anchors. The localized day and day-part WORDS a narrative uses come from
/// <c>day_name_reference</c> and the approved-vocabulary glossary; this block only fixes which day and
/// which day-part a block is.
/// </para>
/// </summary>
public static class BlockLocalLabels
{
    // Indexed by local-hour / 6, mirroring StructuredReportRenderer.PartOf (00-06 / 06-12 / 12-18 / 18-24).
    private static readonly string[] PartNames = ["early hours", "morning", "afternoon", "evening"];

    /// <summary>
    /// Emits one line per block, earliest first, or an empty string when there are no blocks.
    /// </summary>
    /// <param name="snapshot">The provisional forecast body whose blocks are labelled.</param>
    /// <param name="tz">Locality timezone — the reader sees each block on its local clock.</param>
    public static string Build(ForecastSnapshotBody snapshot, TimeZoneInfo tz)
    {
        if (snapshot.Blocks.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine("block_local_labels (each forecast block's LOCAL date, weekday, day-part and clock span, "
            + "keyed by its startUtc — name a block's time from its label here; never convert startUtc to "
            + "local time yourself):");
        foreach (var block in snapshot.Blocks.OrderBy(b => b.StartUtc))
        {
            // Blocks carry kind-unspecified UTC instants (the convention DayNameReference also follows).
            var startUtc = DateTime.SpecifyKind(block.StartUtc, DateTimeKind.Utc);
            var local = TimeZoneInfo.ConvertTimeFromUtc(startUtc, tz);
            var part = local.Hour / 6;
            // The span ENDS at the next local boundary. Deriving it from startUtc + 6h would be wrong on a
            // DST day, when the local 00-06 block is 5 or 7 UTC hours long.
            var endHour = (part + 1) * 6;
            sb.Append("  ")
              .Append(startUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))
              .Append(" = ")
              .Append(local.ToString("ddd yyyy-MM-dd", CultureInfo.InvariantCulture))
              .Append(' ').Append(PartNames[part])
              .Append(" (").Append(local.ToString("HH:mm", CultureInfo.InvariantCulture))
              .Append('-').Append(endHour.ToString("00", CultureInfo.InvariantCulture)).Append(":00)")
              .AppendLine();
        }
        return sb.ToString();
    }
}