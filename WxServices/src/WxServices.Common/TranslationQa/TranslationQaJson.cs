using System.Text.Encodings.Web;
using System.Text.Json;

namespace WxServices.Common.TranslationQa;

/// <summary>
/// Shared JSON options for the translation-QA judge package. Lives in Common (WX-219) so the producer
/// (the TranslationQa tool) writes and the consumer (the WxManager review tab) reads with identical
/// settings — same casing, same escaping — and the two can never drift.
/// </summary>
public static class TranslationQaJson
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

    /// <summary>Reader: tolerant of property-name casing in a pasted model reply or a hand-edited file.</summary>
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