using System.Text.Encodings.Web;
using System.Text.Json;

namespace WxReport.Tools.TranslationQa;

/// <summary>Shared JSON options for the translation-QA tool.</summary>
internal static class TranslationQaJson
{
    /// <summary>
    /// Writer: indented, camelCase, native script preserved literally (ü, ñ, ĵ, future CJK) instead of
    /// \uXXXX escaping. The payloads are read by humans and pasted into an LLM, never embedded in HTML,
    /// so relaxed escaping is safe and far more readable.
    /// </summary>
    public static readonly JsonSerializerOptions Write = Frozen(new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    /// <summary>Reader: tolerant of property-name casing in a pasted model reply.</summary>
    public static readonly JsonSerializerOptions Read = Frozen(new()
    {
        PropertyNameCaseInsensitive = true,
    });

    // These are shared, process-wide instances; freeze them at construction so they can't be mutated
    // later by accident (they would freeze on first use anyway — this just makes the intent explicit).
    private static JsonSerializerOptions Frozen(JsonSerializerOptions options)
    {
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}