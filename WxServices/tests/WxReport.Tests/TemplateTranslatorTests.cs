using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using MetarParser.Data.Entities;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

// WX-172: the fail-closed validator for Claude's template-translation output. These tests
// lock the output-integrity contract: a well-formed return (including one that self-flags a
// not-representable token) is accepted; every malformed return is rejected with a JsonException
// so the retry/Failure path never persists a broken phrase.
public class TemplateTranslatorTests
{
    // A 3-token baseline: a plain phrase, a format string with placeholders, and a Hint row.
    private static IReadOnlyDictionary<string, LanguageTemplate> Baseline() => new[]
    {
        new LanguageTemplate { Token = "rain_light", Phrase = "light rain", ContextInfo = "It is light rain now.", ContextKind = TemplateContextKind.Example, Representable = true },
        new LanguageTemplate { Token = "x_and_y", Phrase = "{0} and {1}", ContextInfo = "rain and fog", ContextKind = TemplateContextKind.Example, Representable = true },
        new LanguageTemplate { Token = "deg_unit", Phrase = "degrees", ContextInfo = "English unit word", ContextKind = TemplateContextKind.Hint, Representable = true },
    }.ToDictionary(t => t.Token, StringComparer.Ordinal);

    private static object Entry(string token, string phrase, string ctx, bool representable, string? note = null) =>
        new { token, phrase, translatedContext = ctx, representable, note };

    private static JsonElement Payload(string culture, params object[] entries) =>
        JsonSerializer.SerializeToElement(new { cultureName = culture, translations = entries });

    // A complete, valid 3-token translation set, with one entry optionally overridden — so a
    // test isolates the rule it targets and can't false-green on a missing-token rejection.
    private static object[] FullSet(object? rainLight = null, object? xAndY = null, object? degUnit = null) => new[]
    {
        rainLight ?? Entry("rain_light", "pluie légère", "ctx", true),
        xAndY ?? Entry("x_and_y", "{0} et {1}", "ctx", true),
        degUnit ?? Entry("deg_unit", "degrés", "ctx", true),
    };

    [Fact]
    public void Accepts_a_complete_well_formed_set()
    {
        var (culture, rows) = TemplateTranslator.Validate(
            Payload("fr-FR",
                Entry("rain_light", "pluie légère", "Il pleut légèrement.", true),
                Entry("x_and_y", "{0} et {1}", "pluie et brouillard", true),
                Entry("deg_unit", "degrees", "English unit word", true)),
            Baseline());

        Assert.Equal("fr-FR", culture);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.True(r.Representable));
    }

    [Fact]
    public void Accepts_a_not_representable_token_with_a_note_as_BLOCKED()
    {
        var (_, rows) = TemplateTranslator.Validate(
            Payload("de-DE",
                Entry("rain_light", "leichter Regen", "Es regnet leicht.", true),
                Entry("x_and_y", "", "n/a", false, "German word order can't fill this slot by substitution."),
                Entry("deg_unit", "degrees", "English unit word", true)),
            Baseline());

        Assert.Equal(3, rows.Count);                                   // the sibling rows survive the mixed batch
        var blocked = rows.Single(r => !r.Representable);
        Assert.Equal("x_and_y", blocked.Token);
        Assert.Equal("", blocked.Phrase);
        Assert.False(string.IsNullOrWhiteSpace(blocked.Note));
        Assert.All(rows.Where(r => r.Token != "x_and_y"), r => Assert.True(r.Representable));
    }

    [Fact]
    public void Accepts_a_representable_token_carrying_a_caveat_note()
    {
        var (_, rows) = TemplateTranslator.Validate(
            Payload("fr-FR", FullSet(rainLight: Entry("rain_light", "pluie légère", "ctx", true, "regional term"))),
            Baseline());
        Assert.Equal("regional term", rows.Single(r => r.Token == "rain_light").Note);
    }

    [Fact]
    public void Rejects_a_missing_token()
    {
        Assert.Throws<JsonException>(() => TemplateTranslator.Validate(
            Payload("fr-FR",
                Entry("rain_light", "pluie légère", "ctx", true),
                Entry("x_and_y", "{0} et {1}", "ctx", true)),   // deg_unit missing
            Baseline()));
    }

    [Fact]
    public void Rejects_an_unknown_extra_token()
    {
        Assert.Throws<JsonException>(() => TemplateTranslator.Validate(
            Payload("fr-FR",
                Entry("rain_light", "pluie légère", "ctx", true),
                Entry("x_and_y", "{0} et {1}", "ctx", true),
                Entry("deg_unit", "degrés", "ctx", true),
                Entry("not_a_token", "x", "ctx", true)),
            Baseline()));
    }

    [Fact]
    public void Rejects_a_duplicate_token()
    {
        Assert.Throws<JsonException>(() => TemplateTranslator.Validate(
            Payload("fr-FR",
                Entry("rain_light", "pluie légère", "ctx", true),
                Entry("rain_light", "pluie", "ctx", true),
                Entry("x_and_y", "{0} et {1}", "ctx", true),
                Entry("deg_unit", "degrés", "ctx", true)),
            Baseline()));
    }

    [Fact]
    public void Rejects_a_dropped_placeholder()
    {
        Assert.Throws<JsonException>(() => TemplateTranslator.Validate(
            Payload("fr-FR",
                Entry("rain_light", "pluie légère", "ctx", true),
                Entry("x_and_y", "{0} seulement", "ctx", true),   // dropped {1}
                Entry("deg_unit", "degrés", "ctx", true)),
            Baseline()));
    }

    [Fact]
    public void Accepts_reordered_placeholders()
    {
        // Grammar may reorder the data slots; the multiset is unchanged, so this is valid.
        var (_, rows) = TemplateTranslator.Validate(
            Payload("fr-FR",
                Entry("rain_light", "pluie légère", "ctx", true),
                Entry("x_and_y", "{1} et {0}", "ctx", true),
                Entry("deg_unit", "degrés", "ctx", true)),
            Baseline());
        Assert.Equal("{1} et {0}", rows.Single(r => r.Token == "x_and_y").Phrase);
    }

    [Fact]
    public void Rejects_an_empty_phrase_on_a_representable_token()
    {
        Assert.Throws<JsonException>(() => TemplateTranslator.Validate(
            Payload("fr-FR",
                Entry("rain_light", "", "ctx", true),
                Entry("x_and_y", "{0} et {1}", "ctx", true),
                Entry("deg_unit", "degrés", "ctx", true)),
            Baseline()));
    }

    [Fact]
    public void Rejects_a_not_representable_token_without_a_note()
    {
        Assert.Throws<JsonException>(() => TemplateTranslator.Validate(
            Payload("fr-FR",
                Entry("rain_light", "pluie légère", "ctx", true),
                Entry("x_and_y", "", "ctx", false),   // no note
                Entry("deg_unit", "degrés", "ctx", true)),
            Baseline()));
    }

    [Fact]
    public void Rejects_a_control_char_in_a_phrase()
    {
        Assert.Throws<JsonException>(() => TemplateTranslator.Validate(
            Payload("fr-FR",
                Entry("rain_light", "pluie\nlégère", "ctx", true),   // embedded newline
                Entry("x_and_y", "{0} et {1}", "ctx", true),
                Entry("deg_unit", "degrés", "ctx", true)),
            Baseline()));
    }

    [Fact]
    public void Rejects_a_control_char_in_a_blocked_tokens_context()
    {
        // The blocked path gets the same char hygiene as the representable path (no silent pass).
        Assert.Throws<JsonException>(() => TemplateTranslator.Validate(
            Payload("fr-FR",
                Entry("rain_light", "pluie légère", "ctx", true),
                Entry("x_and_y", "", "bad\u0000ctx", false, "blocked"),   // NUL in context
                Entry("deg_unit", "degrés", "ctx", true)),
            Baseline()));
    }

    [Fact]
    public void Rejects_an_over_length_phrase()
    {
        Assert.Throws<JsonException>(() => TemplateTranslator.Validate(
            Payload("fr-FR",
                Entry("rain_light", new string('x', 501), "ctx", true),   // > 500
                Entry("x_and_y", "{0} et {1}", "ctx", true),
                Entry("deg_unit", "degrés", "ctx", true)),
            Baseline()));
    }

    [Fact]
    public void Rejects_an_added_placeholder_on_a_zero_placeholder_phrase()
    {
        // rain_light's baseline ("light rain") has no placeholder; a translation must not add one.
        Assert.Throws<JsonException>(() => TemplateTranslator.Validate(
            Payload("fr-FR", FullSet(rainLight: Entry("rain_light", "pluie {0}", "ctx", true))),
            Baseline()));
    }

    [Fact]
    public void Rejects_a_duplicated_placeholder()
    {
        // "{0} et {0}" preserves the SET {0} but not the multiset of "{0} and {1}" — must reject.
        Assert.Throws<JsonException>(() => TemplateTranslator.Validate(
            Payload("fr-FR", FullSet(xAndY: Entry("x_and_y", "{0} et {0}", "ctx", true))),
            Baseline()));
    }

    [Fact]
    public void Rejects_an_over_length_context()
    {
        Assert.Throws<JsonException>(() => TemplateTranslator.Validate(
            Payload("fr-FR", FullSet(rainLight: Entry("rain_light", "pluie", new string('x', 1001), true))),
            Baseline()));
    }

    [Fact]
    public void Rejects_an_over_length_note_on_a_blocked_token()
    {
        Assert.Throws<JsonException>(() => TemplateTranslator.Validate(
            Payload("fr-FR", FullSet(xAndY: Entry("x_and_y", "", "ctx", false, new string('x', 1001)))),
            Baseline()));
    }

    [Fact]
    public void Rejects_an_empty_or_over_length_culture()
    {
        // Full token set so the ONLY possible rejection is the culture-length rule (no false-green
        // on a missing-token error).
        Assert.Throws<JsonException>(() => TemplateTranslator.Validate(
            Payload("", FullSet()), Baseline()));
        Assert.Throws<JsonException>(() => TemplateTranslator.Validate(
            Payload(new string('x', 21), FullSet()), Baseline()));
    }

    [Fact]
    public void Rejects_a_malformed_culture_tag()
    {
        // Non-empty, short, control-char-free, but not a real culture (a space is invalid in a
        // culture name) — must fail closed rather than persist as the date/number-format tag.
        Assert.Throws<JsonException>(() => TemplateTranslator.Validate(
            Payload("fr FR", FullSet()), Baseline()));
    }
}