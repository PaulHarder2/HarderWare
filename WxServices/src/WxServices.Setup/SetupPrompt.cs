using System.Globalization;

namespace WxServices.Setup;

/// <summary>Parse-and-validate signature shared by the validators below (TryParse-shaped, plus a reason).</summary>
public delegate bool TryParser<T>(string? raw, out T value, out string error);

/// <summary>
/// Validation of the operator-entered foundational values (WX-314, AC-2). Pure — unit-tested. The
/// prompting loop that uses these lives in <see cref="ConsolePrompter"/>; keeping the rules here is
/// what makes "reject bad input" provable without a console.
/// </summary>
public static class SetupValidators
{
    /// <summary>
    /// Four ASCII letters, trimmed and upper-cased (e.g. <c>KDFW</c>). This checks the code's
    /// <em>shape</em> only — a well-formed but nonexistent code (<c>ZZZZ</c>) is accepted here and
    /// surfaces later as a fetch failure. Proving existence would need a station lookup at setup
    /// time, which setup deliberately does not do.
    /// </summary>
    public static bool TryParseIcao(string? raw, out string value, out string error)
    {
        value = string.Empty;
        var candidate = raw?.Trim().ToUpperInvariant() ?? string.Empty;
        if (candidate.Length != 4 || !candidate.All(c => c is >= 'A' and <= 'Z'))
        {
            error = "ICAO must be exactly four letters (e.g. KDFW).";
            return false;
        }

        value = candidate;
        error = string.Empty;
        return true;
    }

    /// <summary>A latitude in [-90, 90].</summary>
    public static bool TryParseLatitude(string? raw, out double value, out string error) =>
        TryParseInRange(raw, -90, 90, "Latitude must be a number from -90 to 90.", out value, out error);

    /// <summary>A longitude in [-180, 180].</summary>
    public static bool TryParseLongitude(string? raw, out double value, out string error) =>
        TryParseInRange(raw, -180, 180, "Longitude must be a number from -180 to 180.", out value, out error);

    /// <summary>A degree span strictly greater than zero (the fetch bounding box).</summary>
    public static bool TryParsePositiveDegrees(string? raw, out double value, out string error)
    {
        if (!TryParseNumber(raw, out value) || value <= 0)
        {
            error = "Must be a number greater than 0 (degrees).";
            value = 0;
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// The plot map extent. Handed verbatim to the plotting script's <c>--extent</c> argument, where
    /// an empty value is legal and simply omits the argument — so this trims and accepts anything
    /// rather than imposing a shape the plotting side does not require.
    /// </summary>
    public static bool TryParseMapExtent(string? raw, out string value, out string error)
    {
        value = raw?.Trim() ?? string.Empty;
        error = string.Empty;
        return true;
    }

    /// <summary>Any non-blank password (complexity is the SQL Server instance policy's business, not ours).</summary>
    public static bool TryParsePassword(string? raw, out string value, out string error)
    {
        value = raw ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Password cannot be blank.";
            value = string.Empty;
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Cross-field check on the region box: returns an error message, or <c>null</c> when the bounds
    /// are properly ordered. A degenerate (zero-height or zero-width) box is rejected too — it would
    /// silently fetch nothing.
    /// </summary>
    public static string? ValidateRegion(double south, double north, double west, double east)
    {
        if (south >= north)
            return $"Region south ({Num(south)}) must be less than north ({Num(north)}).";
        if (west >= east)
            return $"Region west ({Num(west)}) must be less than east ({Num(east)}).";
        return null;
    }

    private static bool TryParseInRange(
        string? raw, double min, double max, string message, out double value, out string error)
    {
        if (!TryParseNumber(raw, out value) || value < min || value > max)
        {
            error = message;
            value = 0;
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryParseNumber(string? raw, out double value) =>
        double.TryParse(
            raw?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string Num(double value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Drives the interactive prompts (WX-314, AC-2), re-asking until each answer validates. Console IO
/// is injected so the loop — including the re-prompt behaviour — is unit-testable with a scripted
/// answer sequence and no human at a keyboard.
/// </summary>
public sealed class ConsolePrompter
{
    private readonly Func<string?> _readLine;
    private readonly Action<string> _write;
    private readonly Func<string?> _readSecret;

    /// <param name="readLine">Reads one answer; <c>null</c> means the input stream ended.</param>
    /// <param name="write">Emits a prompt or an error line.</param>
    /// <param name="readSecret">Reads a password without echoing; defaults to <paramref name="readLine"/>.</param>
    public ConsolePrompter(Func<string?> readLine, Action<string> write, Func<string?>? readSecret = null)
    {
        _readLine = readLine;
        _write = write;
        _readSecret = readSecret ?? readLine;
    }

    /// <summary>Prompts for every foundational location value, in the order the operator expects to supply them.</summary>
    public FoundationalInputs PromptFoundational()
    {
        var icao = Ask<string>("Home airport ICAO code (4 letters, e.g. KDFW)", SetupValidators.TryParseIcao);
        var latitude = Ask<double>("Home latitude (-90 to 90)", SetupValidators.TryParseLatitude);
        var longitude = Ask<double>("Home longitude (-180 to 180)", SetupValidators.TryParseLongitude);
        var boundingBox = Ask<double>("Fetch bounding-box size in degrees (> 0)", SetupValidators.TryParsePositiveDegrees);

        double south, north, west, east;
        while (true)
        {
            south = Ask<double>("Region south latitude", SetupValidators.TryParseLatitude);
            north = Ask<double>("Region north latitude", SetupValidators.TryParseLatitude);
            west = Ask<double>("Region west longitude", SetupValidators.TryParseLongitude);
            east = Ask<double>("Region east longitude", SetupValidators.TryParseLongitude);

            // Ordering is a property of the four together, so a bad set re-asks for all four
            // rather than leaving the operator to guess which single value we disliked.
            var problem = SetupValidators.ValidateRegion(south, north, west, east);
            if (problem is null)
                break;
            _write($"  ! {problem} Re-entering the region bounds.");
        }

        var mapExtent = Ask<string>("Map extent for plots (blank for the plot default)", SetupValidators.TryParseMapExtent);

        return new FoundationalInputs(
            icao, latitude, longitude, boundingBox, south, north, west, east, mapExtent);
    }

    /// <summary>Prompts (without echo, when a secret reader was supplied) for the SQL login's password.</summary>
    public string PromptPassword(string sqlLogin)
    {
        while (true)
        {
            _write($"  Password to set for SQL login '{sqlLogin}': ");
            var raw = _readSecret() ?? throw new EndOfStreamException(
                $"Input ended while waiting for the '{sqlLogin}' password.");
            if (SetupValidators.TryParsePassword(raw, out var password, out var error))
                return password;
            _write($"  ! {error}");
        }
    }

    private T Ask<T>(string prompt, TryParser<T> parse)
    {
        while (true)
        {
            _write($"  {prompt}: ");
            var raw = _readLine() ?? throw new EndOfStreamException(
                $"Input ended while waiting for: {prompt}");
            if (parse(raw, out var value, out var error))
                return value;
            _write($"  ! {error}");
        }
    }
}