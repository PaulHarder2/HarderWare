// METAR / SPECI parser.
// Implements FM 15 / FM 16 as specified in WMO Manual 306, Volume I.1.

using System.Text.RegularExpressions;

using WxServices.Logging;

namespace MetarParser;

/// <summary>
/// Static parser for METAR and SPECI aerodrome weather reports.
/// Implements the FM 15 / FM 16 code forms defined in WMO Manual 306,
/// Volume I.1, with additional support for US-format statute-mile visibility
/// and A-prefix (inches of mercury) altimeter settings.
/// <para>
/// Parsing is performed left-to-right over a whitespace-tokenised copy of the
/// input string.  Each group consumer peeks at the next token, applies its
/// compiled regular expression, and either advances the cursor and returns a
/// decoded value, or leaves the stream unchanged and returns <see langword="null"/>.
/// </para>
/// </summary>
public static class MetarParser
{
    // ── top-level entry point ────────────────────────────────────────────────

    /// <summary>
    /// Parses a raw METAR or SPECI report string and returns a fully decoded
    /// <see cref="MetarReport"/>.
    /// </summary>
    /// <param name="raw">
    /// The complete report string exactly as distributed, including the report
    /// type identifier.  Leading/trailing whitespace, runs of internal whitespace,
    /// and a trailing <c>=</c> character are all tolerated.
    /// </param>
    /// <returns>
    /// A <see cref="MetarReport"/> whose properties reflect every group that could
    /// be decoded.  Groups that could not be matched are collected in
    /// <see cref="MetarReport.UnparsedGroups"/>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="raw"/> is <see langword="null"/>, empty, or
    /// consists only of whitespace.
    /// </exception>
    /// <exception cref="MetarParseException">
    /// Thrown when the mandatory header fields (report type, station identifier,
    /// or date/time group) are absent.
    /// </exception>
    public static MetarReport Parse(string raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);

        // Normalise: collapse whitespace, strip trailing '='
        var text = Regex.Replace(raw.Trim(), @"\s+", " ").TrimEnd('=');
        var tokens = new TokenStream(text.Split(' '));

        var reportType = ConsumeReportType(tokens)
            ?? throw new MetarParseException("Missing report type (METAR/SPECI).", raw);

        var station = ConsumeStation(tokens)
            ?? throw new MetarParseException("Missing station identifier.", raw);

        var (day, hour, minute) = ConsumeDateTime(tokens, raw)
            ?? throw new MetarParseException("Missing date/time group.", raw);

        var (isAuto, isCorrection) = ConsumeModifiers(tokens);
        var rejected = new List<string>();
        var wind = ConsumeWind(tokens, raw, rejected);
        var variableSector = ConsumeVariableSector(tokens);
        if (wind is not null && variableSector.HasValue)
            wind = wind with { VariableFrom = variableSector.Value.from, VariableTo = variableSector.Value.to };

        var visibility = ConsumeVisibility(tokens);
        var minVis = ConsumeMinimumVisibility(tokens);
        if (visibility is not null && minVis.HasValue)
            visibility = visibility with
            {
                MinimumDistanceMeters = minVis.Value.dist,
                MinimumDirection = minVis.Value.dir
            };

        var rvr = ConsumeAllRvr(tokens);
        var presentWeather = ConsumeWeatherPhenomena(tokens, recentPrefix: false);
        var sky = ConsumeSkyConditions(tokens);
        var temp = ConsumeTemperature(tokens);
        var altimeter = ConsumeAltimeter(tokens);
        var recentWeather = ConsumeWeatherPhenomena(tokens, recentPrefix: true);
        var windShear = ConsumeWindShear(tokens);
        var trend = ConsumeTrend(tokens, raw);
        var remarks = ConsumeRemarks(tokens);
        var unparsed = rejected.Concat(tokens.Remaining()).ToList();

        return new MetarReport
        {
            Raw = raw,
            ReportType = reportType,
            Station = station,
            Day = day,
            Hour = hour,
            Minute = minute,
            IsAuto = isAuto,
            IsCorrection = isCorrection,
            Wind = wind,
            Visibility = visibility,
            Rvr = rvr,
            PresentWeather = presentWeather,
            Sky = sky,
            Temperature = temp,
            Altimeter = altimeter,
            RecentWeather = recentWeather,
            WindShear = windShear,
            Trend = trend,
            Remarks = remarks,
            UnparsedGroups = unparsed,
        };
    }

    // ── individual group parsers ─────────────────────────────────────────────

    /// <summary>
    /// Attempts to consume the report-type token (WMO 306 §15.1).
    /// </summary>
    /// <param name="t">The token stream positioned at the start of the report.</param>
    /// <returns>
    /// <c>"METAR"</c> or <c>"SPECI"</c> if the next token is one of those values;
    /// otherwise <see langword="null"/> and the stream is not advanced.
    /// </returns>
    private static string? ConsumeReportType(TokenStream t)
    {
        if (t.Peek() is "METAR" or "SPECI")
            return t.Next();
        return null;
    }

    /// <summary>
    /// Attempts to consume the four-character station identifier (WMO 306 §15.2).
    /// </summary>
    /// <param name="t">The token stream positioned after the report-type token.</param>
    /// <returns>
    /// The station identifier string if the next token is exactly four characters
    /// consisting of uppercase ASCII letters or digits, with at least one letter
    /// (to exclude pure numeric tokens such as day/time groups); otherwise
    /// <see langword="null"/> and the stream is not advanced.
    /// </returns>
    /// <remarks>
    /// True ICAO location indicators (WMO 306 §15.2) are four uppercase letters,
    /// but many nations — including the United States — assign alphanumeric
    /// identifiers such as <c>K5T9</c>, <c>K1F0</c>, and <c>K3T5</c> to
    /// smaller airports and observing stations that are not assigned a purely
    /// alphabetic ICAO code.  Allowing digits in positions 2–4 accommodates
    /// these identifiers while the mandatory-letter constraint prevents the
    /// day/time token (e.g. <c>221151Z</c>) from being misread as a station.
    /// </remarks>
    private static string? ConsumeStation(TokenStream t)
    {
        if (t.Peek() is { Length: 4 } s &&
            Regex.IsMatch(s, @"^[A-Z0-9]{4}$") &&
            s.Any(char.IsLetter))
            return t.Next();
        return null;
    }

    /// <summary>
    /// Attempts to consume the date/time group in DDHHMMz format (WMO 306 §15.2).
    /// </summary>
    /// <param name="t">The token stream positioned after the station identifier.</param>
    /// <returns>
    /// A tuple of (day, hour, minute) in UTC if the next token matches the pattern
    /// <c>\d{6}Z</c>; otherwise <see langword="null"/> and the stream is not advanced.
    /// </returns>
    private static (int day, int hour, int minute)? ConsumeDateTime(TokenStream t, string raw)
    {
        if (t.Peek() is not { } s || !Regex.IsMatch(s, @"^\d{6}Z$"))
            return null;

        var day = int.Parse(s[..2]);
        var hour = int.Parse(s[2..4]);
        var minute = int.Parse(s[4..6]);

        if (day < 1 || day > 31 || hour > 23 || minute > 59)
        {
            Logger.Warn($"Malformed date/time group '{s}': day={day}, hour={hour}, minute={minute} — values out of valid range. Record: {raw}");
            return null;
        }

        t.Next();
        return (day, hour, minute);
    }

    /// <summary>
    /// Consumes zero, one, or both of the optional modifier tokens
    /// <c>AUTO</c> and <c>COR</c> (WMO 306 §15.3).
    /// Both may appear in the same report; order is not significant.
    /// </summary>
    /// <param name="t">The token stream positioned after the date/time group.</param>
    /// <returns>
    /// A tuple of (auto, correction) boolean flags reflecting which modifiers
    /// were found.  Always returns a value; never advances past non-modifier tokens.
    /// </returns>
    private static (bool auto, bool cor) ConsumeModifiers(TokenStream t)
    {
        bool auto = false, cor = false;
        while (t.Peek() is "AUTO" or "COR")
        {
            if (t.Next() == "AUTO") auto = true;
            else cor = true;
        }
        return (auto, cor);
    }

    /// <summary>
    /// Compiled regular expression for the wind group (WMO 306 §15.3).
    /// Matches: dddff[Gfmfm]KT|MPS  or  VRBff[Gfmfm]KT|MPS
    /// Named captures: dir, spd, gst (optional), unit.
    /// </summary>
    private static readonly Regex WindRe = new(
        @"^(?<dir>VRB|\d{3})(?<spd>\d{2,3})(G(?<gst>\d{2,3}))?(?<unit>KT|MPS)$",
        RegexOptions.Compiled);

    /// <summary>
    /// Attempts to consume the surface wind group (WMO 306 §15.3).
    /// </summary>
    /// <param name="t">The token stream positioned after any modifier tokens.</param>
    /// <returns>
    /// A <see cref="Wind"/> object if the next token matches the wind pattern;
    /// otherwise <see langword="null"/> and the stream is not advanced.
    /// Note: the variable-sector suffix (dndndnVdxdxdx) is handled separately by
    /// <see cref="ConsumeVariableSector"/> and merged into the returned object by the caller.
    /// </returns>
    private static Wind? ConsumeWind(TokenStream t, string raw, List<string>? rejected = null)
    {
        if (t.Peek() is not { } s) return null;
        var m = WindRe.Match(s);
        if (!m.Success) return null;

        var isVrb = m.Groups["dir"].Value == "VRB";
        if (!isVrb)
        {
            var dir = int.Parse(m.Groups["dir"].Value);
            if (dir > 360)
            {
                Logger.Warn($"Invalid wind direction {dir} in token '{s}' — direction exceeds 360°; treating as unparsed. Record: {raw}");
                var tok = t.Next()!;
                rejected?.Add(tok);
                return null;
            }
        }

        t.Next();
        return new Wind
        {
            Direction = isVrb ? null : int.Parse(m.Groups["dir"].Value),
            IsVariable = isVrb,
            Speed = int.Parse(m.Groups["spd"].Value),
            Gust = m.Groups["gst"].Success ? int.Parse(m.Groups["gst"].Value) : null,
            Unit = m.Groups["unit"].Value,
        };
    }

    /// <summary>
    /// Compiled regular expression for the variable wind-direction sector (WMO 306 §15.3).
    /// Matches: dndndnVdxdxdx  (e.g. 250V310)
    /// Named captures: from, to.
    /// </summary>
    private static readonly Regex VarSectorRe = new(
        @"^(?<from>\d{3})V(?<to>\d{3})$", RegexOptions.Compiled);

    /// <summary>
    /// Attempts to consume the optional variable wind-direction sector group
    /// that may follow the main wind group (WMO 306 §15.3).
    /// This group is present when the wind direction varies by more than 60° and
    /// the mean speed is 3 kt or more.
    /// </summary>
    /// <param name="t">The token stream positioned immediately after the wind group.</param>
    /// <returns>
    /// A tuple of (from, to) bearing values in degrees true if the next token
    /// matches the pattern dddVddd; otherwise <see langword="null"/> and the
    /// stream is not advanced.
    /// </returns>
    private static (int from, int to)? ConsumeVariableSector(TokenStream t)
    {
        if (t.Peek() is not { } s) return null;
        var m = VarSectorRe.Match(s);
        if (!m.Success) return null;
        t.Next();
        return (int.Parse(m.Groups["from"].Value), int.Parse(m.Groups["to"].Value));
    }

    /// <summary>
    /// Compiled regular expression for the WMO meter-format visibility group (WMO 306 §15.4).
    /// Matches: [M]VVVV[NDV]  where VVVV is a four-digit distance in meters.
    /// Named captures: lt (optional less-than prefix), dist.
    /// </summary>
    private static readonly Regex VisRe = new(
        @"^(?<lt>M)?(?<dist>\d{4})(NDV)?$", RegexOptions.Compiled);

    /// <summary>
    /// Compiled regular expression for a whole-number US statute-mile visibility token.
    /// Matches: [M]nSM  e.g. 10SM, 7SM, M1SM.
    /// Named captures: lt (optional), n (whole miles).
    /// </summary>
    private static readonly Regex SmWholeRe = new(
        @"^(?<lt>M)?(?<n>\d+)SM$", RegexOptions.Compiled);

    /// <summary>
    /// Compiled regular expression for a fractional US statute-mile visibility token.
    /// Matches: [M]n/dSM  e.g. 1/2SM, 3/4SM, M1/4SM.
    /// Named captures: lt (optional), n (numerator), d (denominator).
    /// </summary>
    private static readonly Regex SmFractionRe = new(
        @"^(?<lt>M)?(?<n>\d+)/(?<d>\d+)SM$", RegexOptions.Compiled);

    /// <summary>
    /// Compiled regular expression used to detect the integer part of a two-token
    /// mixed-number statute-mile visibility (e.g. the <c>"1"</c> in <c>"1 1/2SM"</c>).
    /// Matches any token consisting entirely of digits.
    /// </summary>
    private static readonly Regex SmIntPartRe = new(
        @"^\d+$", RegexOptions.Compiled);

    /// <summary>
    /// Attempts to consume the prevailing visibility group (WMO 306 §15.4),
    /// including the special token <c>CAVOK</c> and all US statute-mile formats.
    /// </summary>
    /// <param name="t">The token stream positioned after any variable-sector group.</param>
    /// <returns>
    /// A <see cref="Visibility"/> object when the next token (or pair of tokens for
    /// mixed-number SM values) is recognised as a visibility group;
    /// otherwise <see langword="null"/> and the stream is not advanced.
    /// </returns>
    private static Visibility? ConsumeVisibility(TokenStream t)
    {
        if (t.Peek() == "CAVOK")
        {
            t.Next();
            return new Visibility { Cavok = true };
        }
        if (t.Peek() is not { } s) return null;

        // Whole statute miles: 10SM, 7SM
        var mw = SmWholeRe.Match(s);
        if (mw.Success)
        {
            t.Next();
            return new Visibility
            {
                DistanceStatuteMiles = double.Parse(mw.Groups["n"].Value),
                LessThan = mw.Groups["lt"].Success,
            };
        }

        // Fractional statute miles: 1/2SM, 3/4SM, M1/4SM
        var mf = SmFractionRe.Match(s);
        if (mf.Success)
        {
            t.Next();
            return new Visibility
            {
                DistanceStatuteMiles = double.Parse(mf.Groups["n"].Value) / double.Parse(mf.Groups["d"].Value),
                LessThan = mf.Groups["lt"].Success,
            };
        }

        // Two-token mixed number: "1 1/2SM", "2 3/4SM"
        if (SmIntPartRe.IsMatch(s) && t.PeekAt(1) is { } s2 && SmFractionRe.IsMatch(s2))
        {
            t.Next();
            t.Next();
            var mf2 = SmFractionRe.Match(s2);
            return new Visibility
            {
                DistanceStatuteMiles = double.Parse(s) +
                                       double.Parse(mf2.Groups["n"].Value) / double.Parse(mf2.Groups["d"].Value),
            };
        }

        // WMO meter format: 9999, 0200, 0800NE
        var m = VisRe.Match(s);
        if (!m.Success) return null;
        t.Next();
        return new Visibility
        {
            DistanceMeters = int.Parse(m.Groups["dist"].Value),
            LessThan = m.Groups["lt"].Success,
            NoDirectionalVariation = s.EndsWith("NDV"),
        };
    }

    /// <summary>
    /// Compiled regular expression for the minimum-visibility sub-group (WMO 306 §15.4).
    /// Matches: VVVV[Dv]  where Dv is a compass-octant direction.
    /// Named captures: dist, dir (optional).
    /// </summary>
    private static readonly Regex MinVisRe = new(
        @"^(?<dist>\d{4})(?<dir>N|NE|E|SE|S|SW|W|NW)?$", RegexOptions.Compiled);

    /// <summary>
    /// Attempts to consume the optional minimum-visibility group that may follow
    /// the prevailing visibility when the minimum differs and is below 1 500 m
    /// (WMO 306 §15.4).
    /// A direction suffix is required to distinguish this group unambiguously from
    /// another four-digit visibility token.
    /// </summary>
    /// <param name="t">The token stream positioned immediately after the visibility group.</param>
    /// <returns>
    /// A tuple of (distance in meters, compass direction) if the next token is a
    /// four-digit value with a directional suffix; otherwise <see langword="null"/>
    /// and the stream is not advanced.
    /// </returns>
    private static (int dist, string? dir)? ConsumeMinimumVisibility(TokenStream t)
    {
        if (t.Peek() is not { } s) return null;
        // Only matches if a direction is present (otherwise ambiguous with main vis group)
        var m = MinVisRe.Match(s);
        if (!m.Success || !m.Groups["dir"].Success) return null;
        t.Next();
        return (int.Parse(m.Groups["dist"].Value), m.Groups["dir"].Value);
    }

    /// <summary>
    /// Compiled regular expression for a runway visual range group (WMO 306 §15.5).
    /// Matches: RDRDR/[M|P]VRVRVRVR[i]  or  RDRDR/VNVNVNVNVVXVXVXVX[i]
    /// Named captures: rwy, lt, p, v1, v2 (optional), trend (optional).
    /// </summary>
    private static readonly Regex RvrRe = new(
        @"^R(?<rwy>[0-9]{2}[LCR]?)/(?<lt>M)?(?<p>P)?(?<v1>\d{4})(V(?<v2>\d{4}))?(?<trend>[UDN])?$",
        RegexOptions.Compiled);

    /// <summary>
    /// Consumes all consecutive runway visual range groups (WMO 306 §15.5).
    /// Up to four RVR groups may appear in a single report.
    /// </summary>
    /// <param name="t">The token stream positioned after any minimum-visibility group.</param>
    /// <returns>
    /// A read-only list of <see cref="RunwayVisualRange"/> objects, one per group found.
    /// Returns an empty list when no RVR groups are present.
    /// </returns>
    private static IReadOnlyList<RunwayVisualRange> ConsumeAllRvr(TokenStream t)
    {
        var list = new List<RunwayVisualRange>();
        while (t.Peek() is { } s && RvrRe.IsMatch(s))
        {
            var m = RvrRe.Match(t.Next()!);
            bool isVar = m.Groups["v2"].Success;
            list.Add(new RunwayVisualRange
            {
                Runway = m.Groups["rwy"].Value,
                MeanMeters = isVar ? null : int.Parse(m.Groups["v1"].Value),
                MinMeters = isVar ? int.Parse(m.Groups["v1"].Value) : null,
                MaxMeters = isVar ? int.Parse(m.Groups["v2"].Value) : null,
                BelowMinimum = m.Groups["lt"].Success,
                AboveMaximum = m.Groups["p"].Success,
                Trend = m.Groups["trend"].Success ? m.Groups["trend"].Value[0] : null,
            });
        }
        return list;
    }

    /// <summary>
    /// Compiled regular expression for a present or recent weather token (WMO 306 §15.6).
    /// Matches the full w'w' construct:
    /// [+|-|VC][descriptor][precipitation*][obscuration][other]
    /// Named captures: int, dsc, pcp, osc, oth — all optional.
    /// </summary>
    private static readonly Regex WeatherRe = new(
        @"^(?<int>\+|-|VC)?" +
        @"(?<dsc>MI|PR|BC|DR|BL|SH|TS|FZ)?" +
        @"(?<pcp>(?:DZ|RA|SN|SG|IC|PL|GR|GS|UP)+)?" +
        @"(?<osc>BR|FG|FU|VA|DU|SA|HZ|PY)?" +
        @"(?<oth>PO|SQ|FC|SS|DS)?$",
        RegexOptions.Compiled);

    /// <summary>
    /// Consumes zero or more consecutive weather-phenomenon tokens (WMO 306 §15.6),
    /// stopping when the next token cannot be decoded as weather.
    /// Handles both present weather (no prefix) and recent weather (<c>RE</c> prefix).
    /// The special token <c>NSW</c> (no significant weather) is consumed and terminates
    /// the sequence.
    /// </summary>
    /// <param name="t">The token stream positioned at the first potential weather token.</param>
    /// <param name="recentPrefix">
    /// When <see langword="true"/>, each token must begin with <c>RE</c>, which is
    /// stripped before matching. Set to <see langword="false"/> for present weather,
    /// <see langword="true"/> for recent weather.
    /// </param>
    /// <returns>
    /// A read-only list of <see cref="WeatherPhenomenon"/> objects decoded from the
    /// stream.  Returns an empty list when no matching tokens are found.
    /// </returns>
    private static IReadOnlyList<WeatherPhenomenon> ConsumeWeatherPhenomena(TokenStream t, bool recentPrefix)
    {
        var list = new List<WeatherPhenomenon>();
        while (true)
        {
            var s = t.Peek();
            if (s is null) break;
            if (recentPrefix)
            {
                if (!s.StartsWith("RE")) break;
                s = s[2..]; // strip the "RE" prefix
            }
            if (s == "NSW") { t.Next(); break; } // NSW = no significant weather
            var m = WeatherRe.Match(s);
            if (!m.Success || s.Length == 0) break;
            // Must have at least one recognised element beyond just intensity
            if (!m.Groups["dsc"].Success && !m.Groups["pcp"].Success &&
                !m.Groups["osc"].Success && !m.Groups["oth"].Success)
                break;
            t.Next();
            var precips = new List<string>();
            if (m.Groups["pcp"].Success)
            {
                var pcp = m.Groups["pcp"].Value;
                for (int i = 0; i < pcp.Length; i += 2)
                    precips.Add(pcp[i..(i + 2)]);
            }
            list.Add(new WeatherPhenomenon
            {
                Intensity = m.Groups["int"].Value,
                Descriptor = m.Groups["dsc"].Success ? m.Groups["dsc"].Value : null,
                Precipitation = precips,
                Obscuration = m.Groups["osc"].Success ? m.Groups["osc"].Value : null,
                Other = m.Groups["oth"].Success ? m.Groups["oth"].Value : null,
            });
        }
        return list;
    }

    /// <summary>
    /// Compiled regular expression for a layered cloud group (WMO 306 §15.7).
    /// Matches: (FEW|SCT|BKN|OVC)hhh[CB|TCU]
    /// Named captures: cov, ht, ct (optional).
    /// </summary>
    private static readonly Regex SkyRe = new(
        @"^(?<cov>FEW|SCT|BKN|OVC)(?<ht>\d{3}|///)(?<ct>CB|TCU)?$",
        RegexOptions.Compiled);

    /// <summary>
    /// Compiled regular expression for a vertical-visibility group (WMO 306 §15.7).
    /// Matches: VVhhh  where hhh is a three-digit height or <c>///</c> (unknown).
    /// Named capture: ht.
    /// </summary>
    private static readonly Regex VertVisRe = new(
        @"^VV(?<ht>\d{3}|///)$", RegexOptions.Compiled);

    /// <summary>
    /// Consumes all consecutive sky-condition and cloud-layer groups (WMO 306 §15.7),
    /// stopping when the next token is not a recognised sky token.
    /// Handles FEW/SCT/BKN/OVC layer groups, vertical-visibility (VV) groups,
    /// and the whole-sky descriptors SKC, CLR, NSC, and NCD.
    /// SKC/CLR/NSC/NCD terminate the loop because they are mutually exclusive with
    /// individual layer groups.
    /// </summary>
    /// <param name="t">The token stream positioned at the first potential sky token.</param>
    /// <returns>
    /// A read-only list of <see cref="SkyCondition"/> objects in the order they
    /// appeared in the report.  Returns an empty list when no sky groups are found.
    /// </returns>
    private static IReadOnlyList<SkyCondition> ConsumeSkyConditions(TokenStream t)
    {
        var list = new List<SkyCondition>();
        while (true)
        {
            var s = t.Peek();
            if (s is null) break;

            if (s is "SKC" or "CLR" or "NSC" or "NCD")
            {
                t.Next();
                list.Add(new SkyCondition { Cover = s });
                break; // these are mutually exclusive with layered cloud groups
            }

            var vm = VertVisRe.Match(s);
            if (vm.Success)
            {
                t.Next();
                list.Add(new SkyCondition
                {
                    Cover = "VV",
                    HeightFeet = vm.Groups["ht"].Value != "///" ? int.Parse(vm.Groups["ht"].Value) * 100 : null,
                    IsVerticalVisibility = true,
                });
                continue;
            }

            var m = SkyRe.Match(s);
            if (!m.Success) break;
            t.Next();
            list.Add(new SkyCondition
            {
                Cover = m.Groups["cov"].Value,
                HeightFeet = m.Groups["ht"].Value != "///" ? int.Parse(m.Groups["ht"].Value) * 100 : null,
                CloudType = m.Groups["ct"].Success ? m.Groups["ct"].Value : null,
            });
        }
        return list;
    }

    /// <summary>
    /// Compiled regular expression for the temperature/dew-point group (WMO 306 §15.8).
    /// Matches: [M]TT/[M]TdTd  where M is a minus-sign prefix.
    /// Named captures: ta, td.
    /// </summary>
    private static readonly Regex TempRe = new(
        @"^(?<ta>M?\d{2})/(?<td>M?\d{2}|//)$", RegexOptions.Compiled);

    /// <summary>
    /// Attempts to consume the temperature and dew-point group (WMO 306 §15.8).
    /// </summary>
    /// <param name="t">The token stream positioned after any sky-condition groups.</param>
    /// <returns>
    /// A <see cref="Temperature"/> object if the next token matches the TT/TdTd pattern;
    /// otherwise <see langword="null"/> and the stream is not advanced.
    /// When the dew point is missing (<c>//</c>), <see cref="Temperature.DewPoint"/>
    /// is set to <see cref="double.NaN"/>.
    /// </returns>
    private static Temperature? ConsumeTemperature(TokenStream t)
    {
        if (t.Peek() is not { } s) return null;
        var m = TempRe.Match(s);
        if (!m.Success) return null;
        t.Next();
        return new Temperature
        {
            Air = ParseMTemp(m.Groups["ta"].Value),
            DewPoint = m.Groups["td"].Value == "//" ? double.NaN : ParseMTemp(m.Groups["td"].Value),
        };
    }

    /// <summary>
    /// Converts a WMO temperature token (with optional <c>M</c> minus prefix) to a
    /// <see cref="double"/> value in degrees Celsius.
    /// </summary>
    /// <param name="s">
    /// A two-character numeric string, optionally preceded by <c>M</c> to indicate
    /// a negative value, e.g. <c>"05"</c>, <c>"M03"</c>.
    /// </param>
    /// <returns>The temperature in degrees Celsius.</returns>
    private static double ParseMTemp(string s) =>
        s.StartsWith('M') ? -double.Parse(s[1..]) : double.Parse(s);

    /// <summary>
    /// Compiled regular expression for the altimeter-setting group (WMO 306 §15.9).
    /// Matches: Q|A followed by four digits.
    /// Named captures: unit, val.
    /// </summary>
    private static readonly Regex AltimeterRe = new(
        @"^(?<unit>[QA])(?<val>\d{4})$", RegexOptions.Compiled);

    /// <summary>
    /// Attempts to consume the altimeter / QNH setting group (WMO 306 §15.9).
    /// Supports both the ICAO/WMO <c>Q</c>-prefix (whole hectopascals) and the
    /// US <c>A</c>-prefix (hundredths of an inch of mercury).
    /// </summary>
    /// <param name="t">The token stream positioned after the temperature group.</param>
    /// <returns>
    /// An <see cref="Altimeter"/> object if the next token is a Q or A group;
    /// otherwise <see langword="null"/> and the stream is not advanced.
    /// A-prefix values are divided by 100 to convert from hundredths of inHg to inHg.
    /// </returns>
    private static Altimeter? ConsumeAltimeter(TokenStream t)
    {
        if (t.Peek() is not { } s) return null;
        var m = AltimeterRe.Match(s);
        if (!m.Success) return null;
        t.Next();
        bool isHpa = m.Groups["unit"].Value == "Q";
        double val = int.Parse(m.Groups["val"].Value);
        if (!isHpa) val /= 100.0; // convert hundredths of inHg to inHg
        return new Altimeter { Value = val, Unit = isHpa ? "hPa" : "inHg" };
    }

    /// <summary>
    /// Consumes all wind-shear groups of the form <c>WS RDRDR/dddffKT</c> or
    /// <c>WS ALL RWY</c> (WMO 306 §15.11).
    /// </summary>
    /// <param name="t">The token stream positioned after the recent-weather groups.</param>
    /// <returns>
    /// A read-only list of runway designator strings (e.g. <c>"28L"</c>) for individual
    /// runway wind-shear reports, or the single entry <c>"ALL RWY"</c> when all runways
    /// are affected.  Returns an empty list when no wind-shear groups are present.
    /// </returns>
    private static IReadOnlyList<string> ConsumeWindShear(TokenStream t)
    {
        var list = new List<string>();
        while (t.Peek() == "WS")
        {
            t.Next(); // consume WS
            if (t.Peek() == "ALL" && t.PeekAt(1) == "RWY")
            {
                t.Next(); t.Next();
                list.Add("ALL RWY");
            }
            else if (t.Peek() is { } rwy && Regex.IsMatch(rwy, @"^R\d{2}[LCR]?/\d{3}\d{2,3}KT$"))
            {
                list.Add(t.Next()!);
            }
        }
        return list;
    }

    /// <summary>
    /// Consumes all TREND forecast sections (WMO 306 §15.12).
    /// A report may contain NOSIG alone, one BECMG, one TEMPO, or BECMG followed by TEMPO.
    /// Each BECMG or TEMPO section is consumed until the next trend keyword, <c>RMK</c>,
    /// or end of stream.
    /// </summary>
    /// <param name="t">The token stream positioned after any wind-shear groups.</param>
    /// <returns>
    /// A read-only list of <see cref="TrendForecast"/> objects decoded from the stream.
    /// Returns an empty list when no trend section is present.
    /// </returns>
    private static IReadOnlyList<TrendForecast> ConsumeTrend(TokenStream t, string raw)
    {
        var list = new List<TrendForecast>();
        while (t.Peek() is "NOSIG" or "BECMG" or "TEMPO")
        {
            var type = t.Next()!;
            if (type == "NOSIG")
            {
                list.Add(new TrendForecast { ChangeType = "NOSIG" });
                continue;
            }

            string? fm = null, tl = null, at = null;
            Wind? tw = null;
            Visibility? tv = null;
            var wx = new List<WeatherPhenomenon>();
            var sky = new List<SkyCondition>();

            // consume time operators and change groups until next TREND keyword or end
            while (t.Peek() is not (null or "NOSIG" or "BECMG" or "TEMPO" or "RMK"))
            {
                var tok = t.Peek()!;
                if (tok.StartsWith("FM") && tok.Length == 6 && IsDigits(tok[2..]))
                { fm = tok[2..]; t.Next(); }
                else if (tok.StartsWith("TL") && tok.Length == 6 && IsDigits(tok[2..]))
                { tl = tok[2..]; t.Next(); }
                else if (tok.StartsWith("AT") && tok.Length == 6 && IsDigits(tok[2..]))
                { at = tok[2..]; t.Next(); }
                else if (ConsumeWind(t, raw) is { } newWind) tw = newWind;
                else if (ConsumeVisibility(t) is { } newVis) tv = newVis;
                else if (tok is "SKC" or "CLR" or "NSC" or "NCD" ||
                         SkyRe.IsMatch(tok) || VertVisRe.IsMatch(tok))
                    sky.AddRange(ConsumeSkyConditions(t));
                else
                {
                    var wp = ConsumeWeatherPhenomena(t, recentPrefix: false);
                    if (wp.Count > 0) wx.AddRange(wp);
                    else break; // unrecognised token — stop consuming trend
                }
            }

            list.Add(new TrendForecast
            {
                ChangeType = type,
                From = fm,
                Until = tl,
                At = at,
                Wind = tw,
                Visibility = tv,
                Weather = wx,
                Sky = sky,
            });
        }
        return list;
    }

    /// <summary>
    /// Attempts to consume the remarks section introduced by the token <c>RMK</c>
    /// (WMO 306 §15.14 and ICAO Annex 3).
    /// All remaining tokens after <c>RMK</c> are treated as free text.
    /// </summary>
    /// <param name="t">The token stream positioned after any trend sections.</param>
    /// <returns>
    /// The remarks content as a single whitespace-joined string if <c>RMK</c> is
    /// present; otherwise <see langword="null"/> and the stream is not advanced.
    /// </returns>
    private static string? ConsumeRemarks(TokenStream t)
    {
        if (t.Peek() != "RMK") return null;
        t.Next();
        return string.Join(" ", t.Remaining());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> if every character in <paramref name="s"/>
    /// is an ASCII decimal digit.
    /// </summary>
    /// <param name="s">The string to test. Must not be <see langword="null"/>.</param>
    private static bool IsDigits(string s) => s.All(char.IsAsciiDigit);
}

/// <summary>
/// A lightweight forward-only cursor over an array of whitespace-separated tokens
/// extracted from a METAR or SPECI report string.
/// Provides non-destructive peek operations so that group parsers can inspect
/// upcoming tokens without committing to consuming them.
/// </summary>
internal sealed class TokenStream
{
    private readonly string[] _tokens;
    private int _pos;

    /// <summary>
    /// Initialises a new <see cref="TokenStream"/> over the supplied token array.
    /// </summary>
    /// <param name="tokens">
    /// The array of tokens to iterate.  Must not be <see langword="null"/>.
    /// The array is not copied; it must not be modified externally during parsing.
    /// </param>
    public TokenStream(string[] tokens) => _tokens = tokens;

    /// <summary>
    /// Returns the token at the current position without advancing the cursor,
    /// or <see langword="null"/> if the stream is exhausted.
    /// </summary>
    public string? Peek() => _pos < _tokens.Length ? _tokens[_pos] : null;

    /// <summary>
    /// Returns the token at <paramref name="offset"/> positions ahead of the
    /// current position without advancing the cursor, or <see langword="null"/>
    /// if that position is beyond the end of the stream.
    /// Used for two-token look-ahead (e.g. mixed-number statute-mile visibility).
    /// </summary>
    /// <param name="offset">
    /// Number of positions ahead to look. 0 is equivalent to <see cref="Peek"/>;
    /// 1 looks one token ahead, and so on.
    /// </param>
    public string? PeekAt(int offset) =>
        (_pos + offset) < _tokens.Length ? _tokens[_pos + offset] : null;

    /// <summary>
    /// Returns the token at the current position and advances the cursor by one.
    /// Returns <see langword="null"/> (without advancing) if the stream is exhausted.
    /// </summary>
    public string? Next() => _pos < _tokens.Length ? _tokens[_pos++] : null;

    /// <summary>
    /// Yields all remaining tokens from the current position to the end of the
    /// stream, advancing the cursor past each one as it is yielded.
    /// After this method returns, the stream is exhausted.
    /// </summary>
    public IEnumerable<string> Remaining()
    {
        while (_pos < _tokens.Length)
            yield return _tokens[_pos++];
    }
}

/// <summary>
/// The exception thrown by <see cref="MetarParser.Parse"/> when the mandatory
/// header fields of a METAR or SPECI report are absent or cannot be decoded.
/// The <see cref="Exception.Message"/> property describes which field is missing.
/// </summary>
public sealed class MetarParseException(string message, string raw)
    : Exception(message)
{
    /// <summary>
    /// The original raw report string that caused the parse failure.
    /// </summary>
    public string Raw { get; } = raw;
}