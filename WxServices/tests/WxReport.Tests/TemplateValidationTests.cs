using WxServices.Common.TranslationQa;

using Xunit;

namespace WxReport.Tests;

// WX-233 — the shared {n}-placeholder guard used by WX-219 copy-to-DB and the vocabulary editor.
public class TemplateValidationTests
{
    [Theory]
    [InlineData("", "", true)]                                   // no placeholders either side
    [InlineData("Wind change", "Windwechsel", true)]            // plain phrases, none required
    [InlineData(", gusting {0}", ", Böen {0}", true)]           // {0} preserved
    [InlineData("{0} at {1}", "{0} mit {1}", true)]             // both preserved
    [InlineData("{1} {0}", "{0} {1}", true)]                    // order/position irrelevant — same set
    [InlineData("{0} {0}", "{0}", true)]                        // source repeats, candidate uses once — same set
    [InlineData(", gusting {0}", "{0}-böig, {0}", true)]        // candidate repeats — multiplicity ignored
    [InlineData(", gusting {0}", ", böig", false)]              // dropped {0}
    [InlineData("Clear", "Klar {0}", false)]                    // added {0}
    [InlineData("{0} at {1}", "{0} mit", false)]                // dropped {1}
    public void PlaceholdersMatch_enforces_the_placeholder_set(string english, string candidate, bool expected) =>
        Assert.Equal(expected, TemplateValidation.PlaceholdersMatch(english, candidate));
}