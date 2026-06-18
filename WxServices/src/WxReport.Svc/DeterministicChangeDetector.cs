using MetarParser.Data.Entities;

using WxInterp;

using WxServices.Common;

namespace WxReport.Svc;

/// <summary>
/// WX-189: computes the "What's changed" set <em>deterministically</em> from
/// (prior committed snapshot, reconciled final_snapshot), so the LLM never
/// authors a structural change it could invent. This is the generative inverse
/// of <c>ForecastReconciler.ValidateChangeSnapshotConsistency</c>: where that
/// validator <em>rejected</em> a Claude-authored change the data did not
/// support, this <em>emits</em> exactly the changes the data does support, and
/// Claude is left only to narrate them (Option C — the change set is computed
/// post-hoc against the snapshot Claude returned, so it is consistent by
/// construction and adds no LLM call).
///
/// <para>
/// Coverage mirrors what the snapshot can carry deterministically:
/// <list type="bullet">
/// <item><b>Precipitation</b> (rain / thunderstorm / mixed / snow / freezing) and
/// <b>standalone severe</b> — the inverse of the WX-148/151 oracle: a per-phenomenon
/// expectation or severeFlag delta over the prior, block by block.</item>
/// <item><b>Temperature</b> and <b>wind</b> — via the same forecaster-tuned thresholds
/// the WX-160 significance gate uses (freeze/thaw at 32 °F, heat-advisory crossing,
/// wind-advisory crossing, per-horizon-tier magnitude), so the band itemizes the same
/// class of change the gate wakes Claude for.</item>
/// </list>
/// Obscuration (fog/haze/smoke/dust) is excluded: <c>GfsSnapshotBuilder</c> never
/// populates it (always <see cref="Obscuration.None"/>), so there is nothing to
/// detect. WindShift (a directional veer) has no wind-direction field in the block
/// and is the sole residual — added deterministically by WX-191.
/// </para>
///
/// <para>
/// Pure function: no I/O, no state. Block matching is by
/// <see cref="ForecastSnapshotBlock.StartUtc"/> (both bodies sit on the same local
/// day-part grid, WX-155). Only the 0–120 h horizon is considered, matching the gate
/// and the per-day extended-forecast grid the reader actually sees.
/// </para>
/// </summary>
internal static class DeterministicChangeDetector
{
    // The five precipitation phenomena, shared 1:1 by name with ChangePhenomenon
    // (the precip arm of that enum). Iterated per-phenomenon so a phantom cannot
    // borrow an unrelated phenomenon's signal (the WX-151 per-phenomenon-severe rule,
    // preserved on the generative side).
    private static readonly PrecipPhenomenon[] PrecipPhenomena =
        Enum.GetValues<PrecipPhenomenon>();

    /// <summary>
    /// Emit the salience-ranked change set for this cycle. Empty on a first send
    /// (no prior to diff) or when nothing material changed within the horizon.
    /// SummaryTokens (<c>ch1</c>, <c>ch2</c>, …) are assigned in ranked order.
    /// </summary>
    /// <param name="prior">The prior committed snapshot (what the recipient last saw), or null on a first send.</param>
    /// <param name="final">The reconciled snapshot Claude returned this cycle.</param>
    /// <param name="cfg">Significance thresholds (shared with the WX-114/160 gate).</param>
    /// <param name="nowUtc">Cycle timestamp; horizon tiers are measured from here.</param>
    /// <param name="tz">Locality timezone, for grouping temperature into local calendar days.</param>
    public static IReadOnlyList<ReportChange> Detect(
        ForecastSnapshotBody? prior, ForecastSnapshotBody final,
        SignificanceGateConfig cfg, DateTime nowUtc, TimeZoneInfo tz)
    {
        ArgumentNullException.ThrowIfNull(final);
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(tz);

        // A first send (null/empty prior) has nothing to compare against — an
        // introductory report carries no "what's changed" band. (Matches the gate's
        // and the oracle's first-send semantics.)
        if (prior is null || prior.Blocks.Count == 0 || final.Blocks.Count == 0)
            return [];

        var priorByStart = new Dictionary<DateTime, ForecastSnapshotBlock>(prior.Blocks.Count);
        foreach (var b in prior.Blocks)
            priorByStart[b.StartUtc] = b;

        var horizonEnd = nowUtc.AddHours(WxThresholds.TierUpperBoundHours[^1]);

        var candidates = new List<Candidate>();
        DetectPrecip(final, priorByStart, nowUtc, horizonEnd, candidates);
        DetectSevere(final, priorByStart, nowUtc, horizonEnd, candidates);
        DetectTemperature(prior, final, cfg, nowUtc, horizonEnd, tz, candidates);
        DetectWind(final, priorByStart, cfg, nowUtc, horizonEnd, candidates);

        return Rank(candidates);
    }

    // ── precipitation (inverse of the WX-148/151 oracle) ──────────────────────
    // For each precip phenomenon, classify every in-horizon block as up / down /
    // unchanged versus its prior counterpart on the same axis the validator used —
    // precipExpectation (None<Possible<Likely<Certain) OR this phenomenon's severeFlag
    // — then group consecutive same-direction blocks into one change window.
    private static void DetectPrecip(
        ForecastSnapshotBody final, IReadOnlyDictionary<DateTime, ForecastSnapshotBlock> priorByStart,
        DateTime nowUtc, DateTime horizonEnd, List<Candidate> candidates)
    {
        foreach (var p in PrecipPhenomena)
        {
            var phenomenon = PrecipToChange(p);
            var runs = new RunBuilder(phenomenon, candidates);
            foreach (var block in InHorizonOrdered(final, nowUtc, horizonEnd))
            {
                if (!priorByStart.TryGetValue(block.StartUtc, out var pri))
                {
                    // No prior counterpart — the block only rolled into the horizon since
                    // the last send, so it is not news by itself (the WX-108 horizon-edge
                    // convention; the gate and the temperature/wind arms skip it too).
                    // Break any open run.
                    runs.Offer(block.StartUtc, dir: null, ChangeTier.Ambient, quantities: []);
                    continue;
                }

                int newE = ExpectOf(block, p);
                int priorE = ExpectOf(pri, p);
                bool newSevere = SevereOf(block, p);
                bool priorSevere = SevereOf(pri, p);

                bool up = newE > priorE || (newSevere && !priorSevere);
                bool down = newE < priorE || (priorSevere && !newSevere);

                // Up takes precedence over a simultaneous down (e.g. expectation rose
                // while severe dropped): a worsening expectation is the headline. A
                // phenomenon absent on both sides (priorE == newE == None) is neither.
                ChangeDirection? dir =
                    up ? (priorE == (int)PrecipExpectation.None && !priorSevere ? ChangeDirection.Appearing : ChangeDirection.Strengthening)
                    : down ? (newE == (int)PrecipExpectation.None && !newSevere ? ChangeDirection.Clearing : ChangeDirection.Weakening)
                    : null;

                // Severe carried by THIS precip block is folded into the phenomenon's
                // own change (a severe thunderstorm is a Thunderstorm strengthening),
                // so the standalone Severe phenomenon is reserved for severe with no
                // precip type (high wind) — see DetectSevere.
                var tier = PrecipTier(block, pri, nowUtc);
                runs.Offer(block.StartUtc, dir, tier, quantities: []);
            }
            runs.Flush();
        }
    }

    // ── standalone severe (non-convective: severe with no precipitation type) ──
    // A severe block that carries a precip phenomenon is already reported as that
    // phenomenon strengthening (DetectPrecip), so emitting a Severe change too would
    // double-narrate it. Standalone Severe captures the residue: a severeFlag block
    // with PrecipPhenomenon == null (a ≥50 kt wind event with no precip).
    private static void DetectSevere(
        ForecastSnapshotBody final, IReadOnlyDictionary<DateTime, ForecastSnapshotBlock> priorByStart,
        DateTime nowUtc, DateTime horizonEnd, List<Candidate> candidates)
    {
        var runs = new RunBuilder(ChangePhenomenon.Severe, candidates);
        foreach (var block in InHorizonOrdered(final, nowUtc, horizonEnd))
        {
            if (!priorByStart.TryGetValue(block.StartUtc, out var pri))
            {
                // Rolled-in block (no prior counterpart) — not news by itself; break the run.
                runs.Offer(block.StartUtc, dir: null, ChangeTier.Safety, quantities: []);
                continue;
            }

            bool newSevere = block.SevereFlag && block.PrecipPhenomenon is null;
            bool priorSevere = pri is { SevereFlag: true, PrecipPhenomenon: null };

            ChangeDirection? dir =
                newSevere && !priorSevere ? ChangeDirection.Appearing
                : priorSevere && !newSevere ? ChangeDirection.Clearing
                : null;

            // Standalone severe is always safety-tier (it exists only for ≥50 kt wind).
            runs.Offer(block.StartUtc, dir, ChangeTier.Safety, quantities: []);
        }
        runs.Flush();
    }

    // ── temperature (per local day, via the gate's tuned thresholds) ──────────
    // Daily aggregation, not per-block: the extended-forecast grid shows one row per
    // local calendar day (WX-112), so a 6-hour wobble that doesn't move the day's high
    // or low is invisible to the reader. One change per day, the most salient signal
    // winning (a freeze/heat crossing outranks a bare magnitude delta).
    private static void DetectTemperature(
        ForecastSnapshotBody prior, ForecastSnapshotBody final,
        SignificanceGateConfig cfg, DateTime nowUtc, DateTime horizonEnd, TimeZoneInfo tz,
        List<Candidate> candidates)
    {
        var curDays = DailyHiLo(final, nowUtc, horizonEnd, tz);
        var priDays = DailyHiLo(prior, nowUtc, horizonEnd, tz);

        foreach (var (day, cur) in curDays)
        {
            // A day that rolled into the horizon has no prior counterpart — not news.
            if (!priDays.TryGetValue(day, out var pri))
                continue;
            int tier = TierOf(cur.FirstStartUtc, nowUtc);
            if (tier < 0)
                continue;

            bool freezeAdd = pri.LoF >= WxThresholds.FreezeDegF && cur.LoF < WxThresholds.FreezeDegF;
            bool thaw = pri.LoF < WxThresholds.FreezeDegF && cur.LoF > WxThresholds.FreezeDegF;
            bool curHeat = cur.HiF >= cfg.HeatAdvisoryDegF;
            bool priHeat = pri.HiF >= cfg.HeatAdvisoryDegF;
            int delta = PerTier(cfg.TempDeltaDegF, tier);
            bool magnitude = Math.Abs(cur.HiF - pri.HiF) >= delta || Math.Abs(cur.LoF - pri.LoF) >= delta;

            // Priority: a safety-grade threshold crossing (freeze/heat) outranks a
            // plain magnitude shift. ChangePhenomenon has no Freeze/Heat member, so all
            // map to Temperature; the crossing's safety weight rides the tier. The
            // Appearing/Clearing/Strengthening/Weakening enum has no native rising/
            // falling, so for Temperature: Appearing = a hazard threshold entered
            // (freeze/heat onset), Clearing = it lifted (thaw / heat ends), Strengthening
            // = a warming magnitude shift, Weakening = a cooling one. The reader-facing
            // meaning is carried by the prose + quantities; direction only feeds salience.
            ChangeDirection dir;
            ChangeTier ctier;
            if (freezeAdd || (curHeat && !priHeat))
            {
                dir = ChangeDirection.Appearing;
                ctier = ChangeTier.Safety;
            }
            else if (thaw || (priHeat && !curHeat))
            {
                dir = ChangeDirection.Clearing;
                ctier = ChangeTier.Plans;  // a hazard lifting is news, but not safety-urgent
            }
            else if (magnitude)
            {
                // Sign from whichever extreme moved further, biased to the high.
                double hiMove = cur.HiF - pri.HiF, loMove = cur.LoF - pri.LoF;
                double dominant = Math.Abs(hiMove) >= Math.Abs(loMove) ? hiMove : loMove;
                dir = dominant >= 0 ? ChangeDirection.Strengthening : ChangeDirection.Weakening;
                ctier = HorizonTier(tier);
            }
            else
            {
                continue;
            }

            var window = new ChangeWindow(cur.FirstStartUtc, cur.LastStartUtc.AddHours(GfsSnapshotBuilder.BlockHours));
            candidates.Add(new Candidate(ChangePhenomenon.Temperature, dir, window, ctier,
                [new ReportQuantity { Kind = QuantityKind.Temp, Value = cur.HiC },
                 new ReportQuantity { Kind = QuantityKind.Temp, Value = cur.LoC }]));
        }
    }

    // ── wind magnitude (per block, via the gate's tuned thresholds) ───────────
    // Wind always exists, so it never "appears" or "clears" — only Strengthening /
    // Weakening. A change fires when the advisory line (cfg.WindAdvisoryKt) is crossed
    // or the per-tier magnitude delta is exceeded; consecutive same-direction blocks
    // group into one window. Sustained only (windKt is sustained-only, WX-160).
    private static void DetectWind(
        ForecastSnapshotBody final, IReadOnlyDictionary<DateTime, ForecastSnapshotBlock> priorByStart,
        SignificanceGateConfig cfg, DateTime nowUtc, DateTime horizonEnd, List<Candidate> candidates)
    {
        var runs = new RunBuilder(ChangePhenomenon.Wind, candidates);
        foreach (var block in InHorizonOrdered(final, nowUtc, horizonEnd))
        {
            int tier = TierOf(block.StartUtc, nowUtc);
            if (tier < 0 || !priorByStart.TryGetValue(block.StartUtc, out var pri))
            {
                runs.Offer(block.StartUtc, dir: null, ChangeTier.Ambient, quantities: []);
                continue;
            }

            int cMax = block.WindKt.Max, pMax = pri.WindKt.Max;
            bool advAdd = pMax < cfg.WindAdvisoryKt && cMax >= cfg.WindAdvisoryKt;
            bool advRemove = pMax >= cfg.WindAdvisoryKt && cMax < cfg.WindAdvisoryKt;
            bool magnitude = Math.Abs(cMax - pMax) >= PerTier(cfg.WindDeltaKt, tier);

            ChangeDirection? dir =
                (advAdd || (cMax > pMax && magnitude)) ? ChangeDirection.Strengthening
                : (advRemove || (cMax < pMax && magnitude)) ? ChangeDirection.Weakening
                : null;

            // Sustained wind reaching the safety floor (≥34 kt) is safety-tier — the
            // same bright line the oracle's safety-backing check uses.
            var ctier = cMax >= WxThresholds.SafetyWindKt ? ChangeTier.Safety : HorizonTier(tier);
            runs.Offer(block.StartUtc, dir, ctier,
                [new ReportQuantity { Kind = QuantityKind.Wind, Value = cMax }]);
        }
        runs.Flush();
    }

    // ── ranking + token assignment ────────────────────────────────────────────
    // Salience order: tier (Safety first), then the WX-81 directional asymmetry
    // (appearing/strengthening weather outranks the equivalent weakening/clearing),
    // then earliest window. SummaryTokens are assigned ch1.. in that order so the
    // band reads most-important-first.
    private static IReadOnlyList<ReportChange> Rank(List<Candidate> candidates)
    {
        var ordered = candidates
            .OrderBy(c => (int)c.Tier)
            .ThenBy(c => DirectionRank(c.Direction))
            .ThenBy(c => c.Window.StartUtc)
            .ToList();

        var result = new List<ReportChange>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            var c = ordered[i];
            result.Add(new ReportChange
            {
                Tier = c.Tier,
                Phenomenon = c.Phenomenon,
                Direction = c.Direction,
                Window = c.Window,
                Quantities = c.Quantities,
                SummaryToken = $"ch{i + 1}",
            });
        }
        return result;
    }

    // Arriving/worsening weather outranks departing/improving weather (WX-81).
    private static int DirectionRank(ChangeDirection d) => d switch
    {
        ChangeDirection.Appearing => 0,
        ChangeDirection.Strengthening => 1,
        ChangeDirection.Weakening => 2,
        ChangeDirection.Clearing => 3,
        _ => 4,  // Shifting (WX-191)
    };

    // ── per-phenomenon / per-block helpers ────────────────────────────────────

    // Tier for a precip block's change: safety when EITHER the new or the prior block
    // carries a safety-grade signal (severe, frozen/freezing precip, or sustained wind
    // ≥ 34 kt — the oracle's safety-backing set), else by horizon. The prior block is
    // checked too because a WEAKENING/CLEARING change removes a hazard the FINAL block no
    // longer carries — and the WX-81 hierarchy counts a newly-removed hazard as safety
    // news at any horizon (clearing a snow/ice event must not de-escalate below Safety).
    private static ChangeTier PrecipTier(ForecastSnapshotBlock final, ForecastSnapshotBlock prior, DateTime nowUtc)
    {
        if (IsSafetyGradePrecip(final) || IsSafetyGradePrecip(prior))
            return ChangeTier.Safety;
        return HorizonTier(TierOf(final.StartUtc, nowUtc));
    }

    private static bool IsSafetyGradePrecip(ForecastSnapshotBlock b) =>
        b.SevereFlag
        || b.PrecipPhenomenon is PrecipPhenomenon.FreezingPrecip or PrecipPhenomenon.Snow
        || b.WindKt.Max >= WxThresholds.SafetyWindKt;

    // Plans when near-horizon (tier 0–1, ≤48 h), Ambient further out — the band's
    // analogue of the WX-81 "plans-affecting when near" rule. Safety is decided by
    // the caller from the hazard signal, never here.
    private static ChangeTier HorizonTier(int tier) => tier is >= 0 and <= 1 ? ChangeTier.Plans : ChangeTier.Ambient;

    private static int ExpectOf(ForecastSnapshotBlock b, PrecipPhenomenon p) =>
        b.PrecipPhenomenon == p ? (int)b.PrecipExpectation : (int)PrecipExpectation.None;

    private static bool SevereOf(ForecastSnapshotBlock b, PrecipPhenomenon p) =>
        b.PrecipPhenomenon == p && b.SevereFlag;

    private static ChangePhenomenon PrecipToChange(PrecipPhenomenon p) => p switch
    {
        PrecipPhenomenon.Rain => ChangePhenomenon.Rain,
        PrecipPhenomenon.Thunderstorm => ChangePhenomenon.Thunderstorm,
        PrecipPhenomenon.Mixed => ChangePhenomenon.Mixed,
        PrecipPhenomenon.Snow => ChangePhenomenon.Snow,
        PrecipPhenomenon.FreezingPrecip => ChangePhenomenon.FreezingPrecip,
        _ => throw new ArgumentOutOfRangeException(nameof(p), p, "Unmapped precipitation phenomenon."),
    };

    private static IEnumerable<ForecastSnapshotBlock> InHorizonOrdered(
        ForecastSnapshotBody body, DateTime nowUtc, DateTime horizonEnd) =>
        body.Blocks
            .Where(b => b.StartUtc.AddHours(GfsSnapshotBuilder.BlockHours) > nowUtc && b.StartUtc < horizonEnd)
            .OrderBy(b => b.StartUtc);

    // Per-local-day high/low in both °F (threshold logic) and °C (canonical
    // quantities), with the day's first/last in-horizon block starts (for the window
    // and tiering). Mirrors SignificanceGate.DailyHiLoDegF.
    private static Dictionary<DateOnly, DayTemp> DailyHiLo(
        ForecastSnapshotBody body, DateTime nowUtc, DateTime horizonEnd, TimeZoneInfo tz)
    {
        var days = new Dictionary<DateOnly, DayTemp>();
        foreach (var b in body.Blocks)
        {
            if (b.StartUtc.AddHours(GfsSnapshotBuilder.BlockHours) <= nowUtc || b.StartUtc >= horizonEnd)
                continue;
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(b.StartUtc, DateTimeKind.Utc), tz);
            var day = DateOnly.FromDateTime(local);
            var hiC = b.TemperatureCelsius.Max;
            var loC = b.TemperatureCelsius.Min;
            if (days.TryGetValue(day, out var cur))
                days[day] = cur.With(hiC, loC, b.StartUtc);
            else
                days[day] = DayTemp.First(hiC, loC, b.StartUtc);
        }
        return days;
    }

    private static int PerTier(int[] arr, int tier) =>
        arr is { Length: > 0 } ? arr[Math.Min(tier, arr.Length - 1)] : 0;

    // Horizon tier (0-based) of a block start, or -1 beyond the last bound. A block
    // already in progress is tier 0. Mirrors SignificanceGate.TierOf.
    private static int TierOf(DateTime startUtc, DateTime nowUtc)
    {
        double hours = (startUtc - nowUtc).TotalHours;
        if (hours < 0) return 0;
        for (int t = 0; t < WxThresholds.TierUpperBoundHours.Length; t++)
            if (hours < WxThresholds.TierUpperBoundHours[t])
                return t;
        return -1;
    }

    private static double CtoF(double celsius) => celsius * 9.0 / 5.0 + 32.0;

    // A change under construction (no SummaryToken yet — assigned in rank order).
    private readonly record struct Candidate(
        ChangePhenomenon Phenomenon, ChangeDirection Direction, ChangeWindow Window,
        ChangeTier Tier, IReadOnlyList<ReportQuantity> Quantities);

    private readonly record struct DayTemp(double HiC, double LoC, DateTime FirstStartUtc, DateTime LastStartUtc)
    {
        public double HiF => CtoF(HiC);
        public double LoF => CtoF(LoC);
        public static DayTemp First(double hiC, double loC, DateTime startUtc) => new(hiC, loC, startUtc, startUtc);
        public DayTemp With(double hiC, double loC, DateTime startUtc) => new(
            Math.Max(HiC, hiC), Math.Min(LoC, loC),
            startUtc < FirstStartUtc ? startUtc : FirstStartUtc,
            startUtc > LastStartUtc ? startUtc : LastStartUtc);
    }

    // Groups a phenomenon's consecutive same-direction blocks into one change window.
    // "Consecutive" = block starts exactly BlockHours apart; a gap, a direction change,
    // or a no-change block closes the current run. The dominant tier across a run is
    // the most severe (Safety < Plans < Ambient by enum order) and its quantities are
    // the run's PEAK (a wind run reports its maximum, not its last block).
    private sealed class RunBuilder(ChangePhenomenon phenomenon, List<Candidate> sink)
    {
        private ChangeDirection? _dir;
        private DateTime _startUtc;
        private DateTime _lastUtc;
        private ChangeTier _tier;
        private IReadOnlyList<ReportQuantity> _quantities = [];

        public void Offer(DateTime startUtc, ChangeDirection? dir, ChangeTier tier, IReadOnlyList<ReportQuantity> quantities)
        {
            bool contiguous = _dir == dir && _dir is not null
                && startUtc == _lastUtc.AddHours(GfsSnapshotBuilder.BlockHours);
            if (contiguous)
            {
                _lastUtc = startUtc;
                _tier = (ChangeTier)Math.Min((int)_tier, (int)tier);
                // Keep the run's PEAK quantity — a strengthening wind run that peaks mid-window
                // must report that peak, not the (lower) trailing block. Precip/severe pass
                // empty quantities, so this is a no-op for them.
                if (quantities.Count > 0 && (_quantities.Count == 0 || quantities[0].Value > _quantities[0].Value))
                    _quantities = quantities;
                return;
            }
            Flush();
            if (dir is null)
                return;
            _dir = dir;
            _startUtc = _lastUtc = startUtc;
            _tier = tier;
            _quantities = quantities;
        }

        public void Flush()
        {
            if (_dir is { } dir)
            {
                var window = new ChangeWindow(_startUtc, _lastUtc.AddHours(GfsSnapshotBuilder.BlockHours));
                sink.Add(new Candidate(phenomenon, dir, window, _tier, _quantities));
            }
            _dir = null;
            _quantities = [];
        }
    }
}