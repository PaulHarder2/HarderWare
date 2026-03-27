// TAF (Terminal Aerodrome Forecast) parser.
// Implements FM 51 as specified in WMO Manual 306, Volume I.1.

using System.Text.RegularExpressions;
using MetarParser;
using WxParser.Logging;

namespace TafParser;

/// <summary>
/// Static parser for TAF (Terminal Aerodrome Forecast) messages.
/// Implements the FM 51 code form defined in WMO Manual 306, Volume I.1.
/// <para>
/// Parsing is performed left-to-right over a whitespace-tokenised copy of the
/// input string.  Each group consumer peeks at the next token, applies its
/// compiled regular expression, and either advances the cursor and returns a
/// decoded value, or leaves the stream unchanged.
/// </para>
/// </summary>
public static class TafParser
{
    // ── entry point ──────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a raw TAF string and returns a fully decoded <see cref="TafReport"/>.
    /// </summary>
    /// <param name="raw">
    /// The complete TAF string, including the <c>TAF</c> type identifier.
    /// Leading/trailing whitespace and a trailing <c>=</c> are tolerated.
    /// </param>
    /// <returns>
    /// A <see cref="TafReport"/> whose properties reflect every group that
    /// could be decoded.  Unrecognised tokens are collected in
    /// <see cref="TafReport.UnparsedGroups"/>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="raw"/> is <see langword="null"/>, empty,
    /// or consists only of whitespace.
    /// </exception>
    /// <exception cref="TafParseException">
    /// Thrown when the mandatory header fields (report type, station, issuance
    /// time, or validity period) are absent or invalid.
    /// </exception>
    public static TafReport Parse(string raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);

        var text   = Regex.Replace(raw.Trim(), @"\s+", " ").TrimEnd('=');
        var tokens = new TokenStream(text.Split(' '));

        var reportType = ConsumeReportType(tokens)
            ?? throw new TafParseException("Missing report type (TAF).");

        var station = ConsumeStation(tokens)
            ?? throw new TafParseException("Missing station identifier.");

        var (issDay, issHour, issMin) = ConsumeIssuanceTime(tokens, raw)
            ?? throw new TafParseException("Missing or invalid issuance time.");

        var (valFromDay, valFromHour, valToDay, valToHour) = ConsumeValidityPeriod(tokens, raw)
            ?? throw new TafParseException("Missing or invalid validity period.");

        var rejected  = new List<string>();
        var wind      = ConsumeWind(tokens, raw, rejected);
        var varSector = ConsumeVariableSector(tokens);
        if (wind is not null && varSector.HasValue)
            wind = wind with { VariableFrom = varSector.Value.from, VariableTo = varSector.Value.to };

        var visibility = ConsumeVisibility(tokens);
        var weather    = ConsumeWeatherPhenomena(tokens);
        var sky        = ConsumeSkyConditions(tokens);
        var periods    = ConsumeChangePeriods(tokens, raw);
        var unparsed   = rejected.Concat(tokens.Remaining()).ToList();

        return new TafReport
        {
            Raw            = raw,
            ReportType     = reportType,
            Station        = station,
            IssuanceDay    = issDay,
            IssuanceHour   = issHour,
            IssuanceMinute = issMin,
            ValidFromDay   = valFromDay,
            ValidFromHour  = valFromHour,
            ValidToDay     = valToDay,
            ValidToHour    = valToHour,
            Wind           = wind,
            Visibility     = visibility,
            Weather        = weather,
            Sky            = sky,
            ChangePeriods  = periods,
            UnparsedGroups = unparsed,
        };
    }

    // ── group consumers ──────────────────────────────────────────────────────

    private static string? ConsumeReportType(TokenStream t)
    {
        if (t.Peek() != "TAF") return null;
        t.Next(); // consume "TAF"
        if (t.Peek() is "AMD" or "COR")
            return $"TAF {t.Next()}";
        return "TAF";
    }

    private static string? ConsumeStation(TokenStream t)
    {
        if (t.Peek() is not { Length: 4 } s) return null;
        if (!Regex.IsMatch(s, @"^[A-Z0-9]{4}$") || !s.Any(char.IsLetter)) return null;
        return t.Next();
    }

    private static readonly Regex IssuanceTimeRe = new(@"^\d{6}Z$", RegexOptions.Compiled);

    private static (int day, int hour, int minute)? ConsumeIssuanceTime(TokenStream t, string raw)
    {
        if (t.Peek() is not { } s || !IssuanceTimeRe.IsMatch(s)) return null;

        var day    = int.Parse(s[..2]);
        var hour   = int.Parse(s[2..4]);
        var minute = int.Parse(s[4..6]);

        if (day < 1 || day > 31 || hour > 23 || minute > 59)
        {
            Logger.Warn($"Malformed TAF issuance time '{s}': day={day}, hour={hour}, minute={minute} — values out of range. Record: {raw}");
            return null;
        }

        t.Next();
        return (day, hour, minute);
    }

    private static readonly Regex ValidityRe = new(
        @"^(?<fd>\d{2})(?<fh>\d{2})/(?<td>\d{2})(?<th>\d{2})$",
        RegexOptions.Compiled);

    private static (int fromDay, int fromHour, int toDay, int toHour)? ConsumeValidityPeriod(TokenStream t, string raw)
    {
        if (t.Peek() is not { } s) return null;
        var m = ValidityRe.Match(s);
        if (!m.Success) return null;

        var fd = int.Parse(m.Groups["fd"].Value);
        var fh = int.Parse(m.Groups["fh"].Value);
        var td = int.Parse(m.Groups["td"].Value);
        var th = int.Parse(m.Groups["th"].Value);

        if (fd < 1 || fd > 31 || fh > 24 || td < 1 || td > 31 || th > 24)
        {
            Logger.Warn($"Malformed TAF validity period '{s}' — values out of range. Record: {raw}");
            return null;
        }

        t.Next();
        return (fd, fh, td, th);
    }

    private static readonly Regex WindRe = new(
        @"^(?<dir>VRB|\d{3})(?<spd>\d{2,3})(G(?<gst>\d{2,3}))?(?<unit>KT|MPS)$",
        RegexOptions.Compiled);

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
                Logger.Warn($"Invalid wind direction {dir} in token '{s}' — exceeds 360°; treating as unparsed. Record: {raw}");
                rejected?.Add(t.Next()!);
                return null;
            }
        }

        t.Next();
        return new Wind
        {
            Direction  = isVrb ? null : int.Parse(m.Groups["dir"].Value),
            IsVariable = isVrb,
            Speed      = int.Parse(m.Groups["spd"].Value),
            Gust       = m.Groups["gst"].Success ? int.Parse(m.Groups["gst"].Value) : null,
            Unit       = m.Groups["unit"].Value,
        };
    }

    private static readonly Regex VarSectorRe = new(
        @"^(?<from>\d{3})V(?<to>\d{3})$", RegexOptions.Compiled);

    private static (int from, int to)? ConsumeVariableSector(TokenStream t)
    {
        if (t.Peek() is not { } s) return null;
        var m = VarSectorRe.Match(s);
        if (!m.Success) return null;
        t.Next();
        return (int.Parse(m.Groups["from"].Value), int.Parse(m.Groups["to"].Value));
    }

    private static readonly Regex VisRe = new(
        @"^(?<lt>M)?(?<dist>\d{4})(NDV)?$", RegexOptions.Compiled);

    // Matches: [M|P]nSM  e.g. 6SM, M1SM, P6SM
    private static readonly Regex VisSmRe = new(
        @"^(?<mod>[MP])?(?<n>\d+)SM$", RegexOptions.Compiled);

    // Matches: n/dSM  e.g. 3/4SM (fractional only)
    private static readonly Regex VisFracSmRe = new(
        @"^(?<n>\d+)/(?<d>\d+)SM$", RegexOptions.Compiled);

    // Matches: w n/dSM  e.g. 1 3/4SM (whole + fraction as two tokens)
    private static readonly Regex VisWholeSmRe = new(
        @"^\d+$", RegexOptions.Compiled);

    private static readonly Regex CavokRe = new(@"^CAVOK$", RegexOptions.Compiled);

    private static Visibility? ConsumeVisibility(TokenStream t)
    {
        if (t.Peek() is not { } s) return null;

        if (CavokRe.IsMatch(s))
        {
            t.Next();
            return new Visibility { Cavok = true };
        }

        var m = VisRe.Match(s);
        if (m.Success)
        {
            t.Next();
            return new Visibility
            {
                DistanceMeters       = int.Parse(m.Groups["dist"].Value),
                LessThan             = m.Groups["lt"].Success,
                NoDirectionalVariation = s.EndsWith("NDV"),
            };
        }

        m = VisSmRe.Match(s);
        if (m.Success)
        {
            t.Next();
            var mod = m.Groups["mod"].Value;
            return new Visibility
            {
                DistanceStatuteMiles = double.Parse(m.Groups["n"].Value),
                LessThan             = mod == "M",
            };
        }

        // Pure fraction: 3/4SM
        m = VisFracSmRe.Match(s);
        if (m.Success)
        {
            t.Next();
            double frac = double.Parse(m.Groups["n"].Value) / double.Parse(m.Groups["d"].Value);
            return new Visibility { DistanceStatuteMiles = frac };
        }

        // Whole + fraction across two tokens: "1" followed by "3/4SM"
        if (VisWholeSmRe.IsMatch(s) && t.Peek2() is { } next && VisFracSmRe.IsMatch(next))
        {
            var whole = int.Parse(t.Next()!);
            var fm    = VisFracSmRe.Match(t.Next()!);
            double frac = double.Parse(fm.Groups["n"].Value) / double.Parse(fm.Groups["d"].Value);
            return new Visibility { DistanceStatuteMiles = whole + frac };
        }

        return null;
    }

    private static readonly Regex WxRe = new(
        @"^(?<int>\+|-|VC)?(?<desc>MI|PR|BC|DR|BL|SH|TS|FZ)?(?<prec>(?:DZ|RA|SN|SG|IC|PL|GR|GS|UP)+)?(?<obsc>BR|FG|FU|VA|DU|SA|HZ|PY)?(?<other>PO|SQ|FC|SS|DS)?$",
        RegexOptions.Compiled);

    private static List<WeatherPhenomenon> ConsumeWeatherPhenomena(TokenStream t)
    {
        var result = new List<WeatherPhenomenon>();
        while (true)
        {
            if (t.Peek() is not { } s) break;
            if (s == "NSW") { t.Next(); break; }   // NSW = No Significant Weather
            var m = WxRe.Match(s);
            if (!m.Success || s.Length == 0) break;
            // Require at least one substantive capture
            if (!m.Groups["desc"].Success && !m.Groups["prec"].Success &&
                !m.Groups["obsc"].Success && !m.Groups["other"].Success) break;
            t.Next();
            var prec = m.Groups["prec"].Success
                ? Enumerable.Range(0, m.Groups["prec"].Value.Length / 2)
                    .Select(i => m.Groups["prec"].Value.Substring(i * 2, 2))
                    .ToList()
                : [];
            result.Add(new WeatherPhenomenon
            {
                Intensity     = m.Groups["int"].Value  is { Length: > 0 } i ? i : "",
                Descriptor    = m.Groups["desc"].Value is { Length: > 0 } d ? d : null,
                Precipitation = prec,
                Obscuration   = m.Groups["obsc"].Value  is { Length: > 0 } o ? o : null,
                Other         = m.Groups["other"].Value is { Length: > 0 } x ? x : null,
            });
        }
        return result;
    }

    private static readonly Regex SkyRe = new(
        @"^(?<cov>FEW|SCT|BKN|OVC|SKC|CLR|NSC|NCD|VV)(?<ht>\d{3})?(?<ct>CB|TCU)?$",
        RegexOptions.Compiled);

    private static List<SkyCondition> ConsumeSkyConditions(TokenStream t)
    {
        var result = new List<SkyCondition>();
        while (t.Peek() is { } s)
        {
            var m = SkyRe.Match(s);
            if (!m.Success) break;
            t.Next();
            result.Add(new SkyCondition
            {
                Cover                = m.Groups["cov"].Value,
                HeightFeet           = m.Groups["ht"].Success ? int.Parse(m.Groups["ht"].Value) * 100 : null,
                CloudType            = m.Groups["ct"].Success ? m.Groups["ct"].Value : null,
                IsVerticalVisibility = m.Groups["cov"].Value == "VV",
            });
        }
        return result;
    }

    // ── change period parsing ────────────────────────────────────────────────

    private static readonly Regex ChangeIndicatorRe = new(
        @"^(BECMG|TEMPO|FM\d{6}|PROB(?:30|40))$", RegexOptions.Compiled);

    private static readonly Regex ValidityGroupRe = new(
        @"^(?<fd>\d{2})(?<fh>\d{2})/(?<td>\d{2})(?<th>\d{2})$",
        RegexOptions.Compiled);

    private static readonly Regex FmTimeRe = new(
        @"^FM(?<d>\d{2})(?<h>\d{2})(?<m>\d{2})$", RegexOptions.Compiled);

    private static List<TafChangePeriod> ConsumeChangePeriods(TokenStream t, string raw)
    {
        var result = new List<TafChangePeriod>();
        while (t.Peek() is { } tok)
        {
            if (!ChangeIndicatorRe.IsMatch(tok)) break;
            string changeType;
            int? fromDay = null, fromHour = null, toDay = null, toHour = null;

            var fmMatch = FmTimeRe.Match(tok);
            if (fmMatch.Success)
            {
                changeType = "FM";
                fromDay  = int.Parse(fmMatch.Groups["d"].Value);
                fromHour = int.Parse(fmMatch.Groups["h"].Value);
                t.Next();
            }
            else if (tok.StartsWith("PROB"))
            {
                changeType = t.Next()!;
                if (t.Peek() == "TEMPO")
                    changeType += $" {t.Next()}";
                // consume validity group
                if (t.Peek() is { } vg)
                {
                    var vgm = ValidityGroupRe.Match(vg);
                    if (vgm.Success)
                    {
                        t.Next();
                        fromDay  = int.Parse(vgm.Groups["fd"].Value);
                        fromHour = int.Parse(vgm.Groups["fh"].Value);
                        toDay    = int.Parse(vgm.Groups["td"].Value);
                        toHour   = int.Parse(vgm.Groups["th"].Value);
                    }
                }
            }
            else
            {
                changeType = t.Next()!; // BECMG or TEMPO
                if (t.Peek() is { } vg)
                {
                    var vgm = ValidityGroupRe.Match(vg);
                    if (vgm.Success)
                    {
                        t.Next();
                        fromDay  = int.Parse(vgm.Groups["fd"].Value);
                        fromHour = int.Parse(vgm.Groups["fh"].Value);
                        toDay    = int.Parse(vgm.Groups["td"].Value);
                        toHour   = int.Parse(vgm.Groups["th"].Value);
                    }
                }
            }

            var periodRejected = new List<string>();
            var wind = ConsumeWind(t, raw, periodRejected);
            var varSector = ConsumeVariableSector(t);
            if (wind is not null && varSector.HasValue)
                wind = wind with { VariableFrom = varSector.Value.from, VariableTo = varSector.Value.to };

            var visibility = ConsumeVisibility(t);
            var weather    = ConsumeWeatherPhenomena(t);
            var sky        = ConsumeSkyConditions(t);

            result.Add(new TafChangePeriod
            {
                ChangeType = changeType,
                FromDay    = fromDay,
                FromHour   = fromHour,
                ToDay      = toDay,
                ToHour     = toHour,
                Wind       = wind,
                Visibility = visibility,
                Weather    = weather,
                Sky        = sky,
            });
        }
        return result;
    }
}

// ── internal token stream ────────────────────────────────────────────────────

/// <summary>
/// A simple forward-only cursor over an array of whitespace-separated tokens.
/// </summary>
internal sealed class TokenStream
{
    private readonly string[] _tokens;
    private int _pos;

    public TokenStream(string[] tokens) => _tokens = tokens;

    /// <summary>Returns the next token without advancing, or <see langword="null"/> if exhausted.</summary>
    public string? Peek() => _pos < _tokens.Length ? _tokens[_pos] : null;

    /// <summary>Returns the token after next without advancing, or <see langword="null"/> if exhausted.</summary>
    public string? Peek2() => _pos + 1 < _tokens.Length ? _tokens[_pos + 1] : null;

    /// <summary>Returns the next token and advances the cursor.</summary>
    public string? Next() => _pos < _tokens.Length ? _tokens[_pos++] : null;

    /// <summary>Returns all unconsumed tokens.</summary>
    public IEnumerable<string> Remaining()
    {
        while (_pos < _tokens.Length)
            yield return _tokens[_pos++];
    }
}
