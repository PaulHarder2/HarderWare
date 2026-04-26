namespace WxViewer;

/// <summary>
/// Lightweight display model for the recipient selector ComboBox.
/// Populated once from the database on startup.
/// </summary>
public sealed record RecipientSummary(
    string RecipientId,
    string Name,
    string Language,
    string? FirstIcao,
    string TempUnit,
    string Timezone)
{
    /// <summary>Text shown in the ComboBox list (e.g. "paulh — Paul Harder (English)").</summary>
    public string DisplayText => $"{RecipientId} — {Name} ({Language})";
}