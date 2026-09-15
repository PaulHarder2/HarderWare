using System.Globalization;
using System.Text;

using MetarParser.Data.Entities;

using WxServices.Common;

namespace WxReport.Svc;

/// <summary>
/// WX-506: renders the computed change set as the facts the change-band ("Why this update") call narrates.
///
/// <para>
/// The band used to be written in the reconciliation call itself, before
/// <see cref="DeterministicChangeDetector"/> ran, so the model had to work out "what the prior said" from
/// six inputs at once. Send 9341 (2026-09-15) reported its GFS-only provisional's value as the prior
/// forecast's and announced a change that never happened. This input carries ONLY the computed changes,
/// each with its local window and its prior → now values (per block for precipitation; per local day for temperature and
/// wind, from <see cref="DayFigures"/> — the comparison the detector decided the change with), so the band call has no
/// second forecast to mistake for the prior.
/// </para>
///
/// <para>
/// Values fold through <see cref="RecipientPrecip"/>, the axis the detector compares on, so a fact never
/// shows a difference the detector did not count (a possible → likely move reads flat here too).
/// </para>
/// </summary>
public static class ChangeSummaryFacts
{
    /// <summary>
    /// Builds the <c>computed_changes</c> block, most significant change first (the detector's rank order),
    /// or an empty string when <paramref name="changes"/> is empty.
    /// </summary>
    /// <param name="changes">The detector's ranked change set.</param>
    /// <param name="prior">The prior committed snapshot the changes were computed against; <see langword="null"/> on a first send.</param>
    /// <param name="final">The reconciled snapshot the changes were computed from.</param>
    /// <param name="tz">Locality timezone, for the local block labels.</param>
    /// <param name="nowUtc">The cycle instant the changes were detected at; temperature facts use the same elapsed-block and horizon cut.</param>
    public static string Build(
        IReadOnlyList<ReportChange> changes, ForecastSnapshotBody? prior, ForecastSnapshotBody final, TimeZoneInfo tz,
        DateTime nowUtc)
    {
        if (changes.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine("computed_changes (most significant first; each is a REAL difference between the prior "
            + "forecast and the new one, computed by a program — narrate these facts and nothing else):");

        for (int i = 0; i < changes.Count; i++)
        {
            var c = changes[i];
            var blocks = final.Blocks
                .Where(b => b.StartUtc >= c.Window.StartUtc && b.StartUtc < c.Window.EndUtc)
                .OrderBy(b => b.StartUtc)
                .ToList();

            sb.Append("  ").Append(i + 1).Append(". ")
              .Append(Snake(c.Phenomenon.ToString())).Append(' ').Append(Snake(c.Direction.ToString()))
              .Append(" [importance: ").Append(TierWords(c.Tier)).AppendLine(" — for your judgment, never write it]");

            if (blocks.Count > 0)
            {
                sb.Append("     window: ").Append(BlockLocalLabels.Label(blocks[0].StartUtc, tz))
                  .Append(" [first block ").Append(BlockLocalLabels.UtcKey(blocks[0].StartUtc)).Append(']');
                if (blocks.Count > 1)
                    sb.Append(" through ").Append(BlockLocalLabels.Label(blocks[^1].StartUtc, tz))
                      .Append(" [last block ").Append(BlockLocalLabels.UtcKey(blocks[^1].StartUtc)).Append(']');
                sb.AppendLine();
            }

            if (c.Phenomenon is ChangePhenomenon.Temperature or ChangePhenomenon.Wind)
                AppendDayFigures(sb, c.Phenomenon, blocks, prior, final, tz, nowUtc);
            else if (c.Phenomenon == ChangePhenomenon.WindShift)
                sb.AppendLine("     wind direction shifts; the forecast data carries no wind direction, so state none");
            else
            {
                foreach (var block in blocks)
                {
                    var before = prior?.Blocks.FirstOrDefault(b => b.StartUtc == block.StartUtc);
                    sb.Append("     ").Append(BlockLocalLabels.Label(block.StartUtc, tz)).Append(": ")
                      .AppendLine(DescribeMove(c.Phenomenon, before, block));
                }
            }
        }

        return sb.ToString();
    }

    // A temperature or wind change states the day's figures as the detector compared them: the forecast last sent,
    // whole day, against the new one with hours already past kept at their sent values. These are the day's
    // high/low and peak wind (not a sub-period reading), exactly as the gate and the detector compared them.
    private static void AppendDayFigures(
        StringBuilder sb, ChangePhenomenon phenomenon, IReadOnlyList<ForecastSnapshotBlock> windowBlocks,
        ForecastSnapshotBody? prior, ForecastSnapshotBody final, TimeZoneInfo tz, DateTime nowUtc)
    {
        var days = prior is null
            ? []
            : DayFigures.Compare(prior, final, nowUtc, nowUtc.AddHours(WxThresholds.TierUpperBoundHours[^1]), tz)
                .ToDictionary(d => d.Day);
        foreach (var date in LocalDates(windowBlocks, tz))
        {
            sb.Append("     ").Append(date.ToString("ddd yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(": ");
            if (!days.TryGetValue(date, out var d))
                sb.AppendLine("(not in prior forecast)");
            else if (phenomenon == ChangePhenomenon.Temperature)
                sb.Append("°C the day's high was ").Append(F(d.Published.HiC)).Append(", now ").Append(F(d.Now.HiC))
                  .Append("; the day's low was ").Append(F(d.Published.LoC)).Append(", now ").Append(F(d.Now.LoC)).AppendLine();
            else
                sb.Append("the day's peak sustained wind kt was ").Append(d.Published.PeakWindKt)
                  .Append(", now ").Append(d.Now.PeakWindKt).AppendLine();
        }
    }

    private static IEnumerable<DateOnly> LocalDates(IEnumerable<ForecastSnapshotBlock> blocks, TimeZoneInfo tz) =>
        blocks.Select(b => DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(b.StartUtc, DateTimeKind.Utc), tz)))
            .Distinct();

    // The block's prior → now value on the axis this change's phenomenon lives on.
    private static string DescribeMove(ChangePhenomenon phenomenon, ForecastSnapshotBlock? before, ForecastSnapshotBlock now)
    {
        string Was(Func<ForecastSnapshotBlock, string> f) => before is null ? "(not in prior forecast)" : f(before);

        return phenomenon switch
        {
            ChangePhenomenon.Fog or ChangePhenomenon.Haze or ChangePhenomenon.Smoke or ChangePhenomenon.Dust =>
                $"obscuration was {Was(b => Snake(b.Obscuration.ToString()))}, now {Snake(now.Obscuration.ToString())}",
            // Precip phenomena and standalone Severe.
            _ => $"precipitation was {Was(Precip)}, now {Precip(now)}",
        };
    }

    // A severe block states no tier: RecipientPrecip pins its expectation to Certain internally, but its
    // recipient wording is always "possible", and "certain" here would invite the "expected" wording.
    private static string Precip(ForecastSnapshotBlock b)
    {
        var phenomenon = RecipientPrecip.Of(b);
        var expectation = RecipientPrecip.Expectation(b);
        if (b.SevereFlag)
            return phenomenon is null ? "severe weather" : $"severe {Snake(phenomenon.Value.ToString())}";
        return phenomenon is null || expectation == PrecipExpectation.None
            ? "none"
            : $"{Snake(expectation.ToString())} {Snake(phenomenon.Value.ToString())}";
    }

    // The significance tier in plain words. Labelled as judgment-only: the 9341 replay with "(tier safety)" put
    // "a safety-tier heat revision" in recipient prose.
    private static string TierWords(ChangeTier tier) => tier switch
    {
        ChangeTier.Safety => "safety-critical",
        ChangeTier.Plans => "plans-affecting",
        _ => "ambient",
    };

    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    // A day figure the compared blocks cannot give (no afternoon block for a high, no morning for a low) is "none".
    private static string F(double? v) => v is { } x ? F(x) : "none";

    // PascalCase enum name → the snake_case the snapshot JSON uses (FreezingPrecip → freezing_precip).
    private static string Snake(string pascal)
    {
        var sb = new StringBuilder(pascal.Length + 4);
        for (int i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascal[i])) sb.Append('_');
            sb.Append(char.ToLowerInvariant(pascal[i]));
        }
        return sb.ToString();
    }
}