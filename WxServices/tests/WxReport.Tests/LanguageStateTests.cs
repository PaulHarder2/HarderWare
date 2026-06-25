using System;

using MetarParser.Data.Entities;

using Xunit;

namespace WxReport.Tests;

// WX-172: the (GeneratedAtUtc, GenerationError) encoding of the four generation states, and
// the IsReady gate the recipient assignment and renderer depend on.
public class LanguageStateTests
{
    private static readonly DateTime When = new(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    // enabled, generatedAt, error                     => expected state
    [InlineData(false, false, false, LanguageGenerationState.Disabled)]
    [InlineData(true, false, false, LanguageGenerationState.Pending)]
    [InlineData(true, true, false, LanguageGenerationState.Ready)]
    [InlineData(true, true, true, LanguageGenerationState.Blocked)]
    [InlineData(true, false, true, LanguageGenerationState.Failed)]
    [InlineData(false, true, true, LanguageGenerationState.Disabled)]   // !IsEnabled short-circuits regardless of error/timestamp
    public void GenerationState_maps_the_columns(bool enabled, bool generated, bool error, LanguageGenerationState expected)
    {
        var lang = new Language
        {
            IsEnabled = enabled,
            GeneratedAtUtc = generated ? When : null,
            GenerationError = error ? "boom" : null,
        };
        Assert.Equal(expected, lang.GenerationState);
        Assert.Equal(expected == LanguageGenerationState.Ready, lang.IsReady);
    }

    [Fact]
    public void A_disabled_language_is_never_ready_even_if_generated()
    {
        var lang = new Language { IsEnabled = false, GeneratedAtUtc = When, GenerationError = null };
        Assert.Equal(LanguageGenerationState.Disabled, lang.GenerationState);
        Assert.False(lang.IsReady);
    }
}