using System.IO;
using System.Windows.Media.Imaging;

namespace WxViewer;

/// <summary>
/// One location entry within a <see cref="MeteogramRun"/>, corresponding to a
/// single recipient's full-period meteogram PNG.
/// </summary>
public sealed class MeteogramItem
{
    /// <summary>ICAO station identifier (e.g. <c>"KDWH"</c>).</summary>
    public string Icao         { get; }

    /// <summary>Human-readable locality name (e.g. <c>"The Woodlands"</c>).</summary>
    public string LocalityName { get; }

    /// <summary>Temperature unit used in the meteogram: <c>"F"</c> or <c>"C"</c>.</summary>
    public string TempUnit     { get; }

    /// <summary>IANA timezone used for the meteogram time axis (e.g. <c>"America/Chicago"</c>).</summary>
    public string Timezone     { get; }

    /// <summary>
    /// Chart title displayed above the meteogram image
    /// (e.g. <c>"KDWH — The Woodlands (°F) · Chicago"</c>).
    /// </summary>
    public string Title        { get; }

    /// <summary>Absolute path to the full-period meteogram PNG.</summary>
    public string FullImagePath { get; }

    /// <summary>
    /// Decoded <see cref="BitmapImage"/> for the full-period meteogram, or
    /// <see langword="null"/> if the file does not exist or could not be loaded.
    /// The image is loaded once on first access and cached.
    /// </summary>
    public BitmapImage? FullImage { get; }

    /// <summary>
    /// Initializes a new <see cref="MeteogramItem"/> and eagerly decodes the
    /// full-period PNG so the file handle is released immediately.
    /// </summary>
    /// <param name="icao">ICAO identifier.</param>
    /// <param name="localityName">Human-readable locality name.</param>
    /// <param name="tempUnit">Temperature unit (<c>"F"</c> or <c>"C"</c>).</param>
    /// <param name="timezone">IANA timezone for the time axis (e.g. <c>"America/Chicago"</c>).</param>
    /// <param name="fullImagePath">Absolute path to the full-period PNG.</param>
    public MeteogramItem(string icao, string localityName, string tempUnit, string timezone, string fullImagePath)
    {
        Icao          = icao;
        LocalityName  = localityName;
        TempUnit      = tempUnit;
        Timezone      = timezone;
        FullImagePath = fullImagePath;
        Title         = $"{icao} — {localityName} (°{tempUnit}) · {TzCity(timezone)}";
        FullImage     = LoadBitmap(fullImagePath);
    }

    /// <summary>
    /// Extracts the city portion from an IANA timezone name for display.
    /// <c>"America/New_York"</c> → <c>"New York"</c>; <c>"UTC"</c> → <c>"UTC"</c>.
    /// </summary>
    private static string TzCity(string tz)
    {
        var slash = tz.LastIndexOf('/');
        return (slash >= 0 ? tz[(slash + 1)..] : tz).Replace('_', ' ');
    }

    private static BitmapImage? LoadBitmap(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource     = new Uri(path, UriKind.Absolute);
            bmp.CacheOption   = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
}
