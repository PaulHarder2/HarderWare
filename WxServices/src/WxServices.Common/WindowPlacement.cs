using System.Text.Json;

namespace WxServices.Common;

/// <summary>
/// Persisted top-level window placement for a WPF app — position, size, and the
/// maximized flag — saved on close and restored on the next launch (WX-291).
/// All coordinates are WPF device-independent pixels.
/// </summary>
/// <remarks>
/// This type is deliberately WPF-free so it lives in <c>WxServices.Common</c> and is
/// unit-testable without a UI. The WPF glue that reads/writes a live <c>Window</c> and
/// resolves the target monitor lives beside the apps (<c>WindowPlacementExtensions</c>);
/// the placement-fitting logic — the part with edge cases worth testing — is all here in
/// <see cref="ClampToWorkArea"/> and <see cref="CenteredDefault"/>.
/// </remarks>
public sealed record WindowPlacement
{
    /// <summary>Left edge, device-independent pixels.</summary>
    public double Left { get; init; }

    /// <summary>Top edge, device-independent pixels.</summary>
    public double Top { get; init; }

    /// <summary>Window width, device-independent pixels.</summary>
    public double Width { get; init; }

    /// <summary>Window height, device-independent pixels.</summary>
    public double Height { get; init; }

    /// <summary>Whether the window was maximized when the placement was captured.</summary>
    public bool Maximized { get; init; }

    /// <summary>First-run default width when no placement has been saved yet: 1920.</summary>
    public const double DefaultWidth = 1920;

    /// <summary>First-run default height when no placement has been saved yet: 1080.</summary>
    public const double DefaultHeight = 1080;

    /// <summary>Directory holding the per-app placement files: <c>%LOCALAPPDATA%\HarderWare</c>.</summary>
    public static string StorageDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HarderWare");

    /// <summary>Returns the placement file path for <paramref name="appName"/> (e.g. <c>"wxviewer"</c>).</summary>
    public static string FilePath(string appName) => Path.Combine(StorageDir, $"{appName}.window.json");

    /// <summary>
    /// Loads the saved placement for <paramref name="appName"/>, or <see langword="null"/> if none
    /// exists or the file is missing, unreadable, or corrupt. Never throws — a bad file is treated
    /// as "no saved state" so the caller falls back to the default.
    /// </summary>
    public static WindowPlacement? Load(string appName)
    {
        try
        {
            var path = FilePath(appName);
            if (!File.Exists(path)) return null;
            var placement = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(path));
            return placement is { IsUsable: true } ? placement : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Saves this placement for <paramref name="appName"/> to <see cref="FilePath"/>, creating
    /// <see cref="StorageDir"/> if needed. Best-effort: any IO error is swallowed so a failed save
    /// can never block the app's shutdown.
    /// </summary>
    public void Save(string appName)
    {
        try
        {
            Directory.CreateDirectory(StorageDir);
            File.WriteAllText(FilePath(appName), JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            // Placement persistence is a convenience, never a correctness requirement.
        }
    }

    /// <summary>
    /// Returns a copy of this placement fitted entirely within the given monitor work area
    /// (all values in the same device-independent pixel space). The size is capped to the work
    /// area so the window is never larger than the monitor, and the origin is shifted so the
    /// whole window — title bar included — sits inside the work area and stays grabbable.
    /// Applied on every launch, to both a restored placement and the first-run default, so a
    /// window can never open off-screen or oversized (WX-291 AC5/AC6).
    /// </summary>
    /// <param name="waLeft">Work-area left edge.</param>
    /// <param name="waTop">Work-area top edge.</param>
    /// <param name="waWidth">Work-area width; must be positive.</param>
    /// <param name="waHeight">Work-area height; must be positive.</param>
    public WindowPlacement ClampToWorkArea(double waLeft, double waTop, double waWidth, double waHeight)
    {
        // Cap the size to the work area first — a window is never larger than the monitor it
        // lands on. A non-positive stored size (corrupt/degenerate) falls back to the default,
        // itself capped to the work area.
        var width = Math.Min(Width > 0 ? Width : DefaultWidth, waWidth);
        var height = Math.Min(Height > 0 ? Height : DefaultHeight, waHeight);

        // Now that width/height are <= the work area, both clamp ranges below are non-empty,
        // so the whole rectangle can always be slid fully inside the work area.
        var left = Math.Clamp(Left, waLeft, waLeft + waWidth - width);
        var top = Math.Clamp(Top, waTop, waTop + waHeight - height);

        return this with { Left = left, Top = top, Width = width, Height = height };
    }

    /// <summary>
    /// Builds the first-run default placement: <see cref="DefaultWidth"/> x <see cref="DefaultHeight"/>,
    /// centered within the given work area and capped to it — so on a monitor smaller than the default
    /// the window shrinks to fit while staying centered. <see cref="Maximized"/> is <see langword="false"/>.
    /// </summary>
    public static WindowPlacement CenteredDefault(double waLeft, double waTop, double waWidth, double waHeight)
    {
        var width = Math.Min(DefaultWidth, waWidth);
        var height = Math.Min(DefaultHeight, waHeight);
        return new WindowPlacement
        {
            Left = waLeft + (waWidth - width) / 2,
            Top = waTop + (waHeight - height) / 2,
            Width = width,
            Height = height,
        };
    }

    /// <summary>
    /// A deserialized placement is usable only if its coordinates are finite and its size is
    /// positive; a hand-corrupted or partially-written file (NaN, Infinity, zero size) is rejected
    /// so the caller falls back to the default rather than applying a nonsensical rectangle.
    /// </summary>
    private bool IsUsable =>
        double.IsFinite(Left) && double.IsFinite(Top) &&
        double.IsFinite(Width) && double.IsFinite(Height) &&
        Width > 0 && Height > 0;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
}