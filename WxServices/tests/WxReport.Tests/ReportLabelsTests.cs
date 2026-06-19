using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

// WX-154/WX-171: ReportLabels.TokenFor is the single source of truth for the token a
// ReportKind renders to, on either surface (subject Title vs header label). Resolved against
// the seed template store, these tests lock the kind->word mapping for both surfaces and both
// shipped languages, so the subject line and the in-report header cannot drift apart (the
// defect that motivated WX-154: a scheduled report reading as an update).

public class ReportLabelsTests
{
    private static readonly LanguageTemplateStore Store = SeedTemplateStore.Build();

    private static string Word(string lang, ReportKind kind, LabelType type) =>
        Store.ForLanguage(lang).Get(ReportLabels.TokenFor(kind, type));

    [Theory]
    [InlineData(ReportKind.Scheduled, "Weather Report")]
    [InlineData(ReportKind.Unscheduled, "Weather Update")]
    [InlineData(ReportKind.Diagnostic, "Diagnostic")]
    public void Title_English_MapsKindToSubjectWord(ReportKind kind, string expected)
        => Assert.Equal(expected, Word("en", kind, LabelType.Title));

    [Theory]
    [InlineData(ReportKind.Scheduled, "Scheduled Report")]
    [InlineData(ReportKind.Unscheduled, "Unscheduled Update")]
    [InlineData(ReportKind.Diagnostic, "Diagnostic")]
    public void Header_English_MapsKindToLabel(ReportKind kind, string expected)
        => Assert.Equal(expected, Word("en", kind, LabelType.Header));

    [Theory]
    [InlineData(ReportKind.Scheduled, "Reporte del tiempo")]
    [InlineData(ReportKind.Unscheduled, "Actualización del tiempo")]
    [InlineData(ReportKind.Diagnostic, "Diagnóstico")]
    public void Title_Spanish_MapsKindToSubjectWord(ReportKind kind, string expected)
        => Assert.Equal(expected, Word("es", kind, LabelType.Title));

    [Theory]
    [InlineData(ReportKind.Scheduled, "Reporte programado")]
    [InlineData(ReportKind.Unscheduled, "Actualización no programada")]
    [InlineData(ReportKind.Diagnostic, "Diagnóstico")]
    public void Header_Spanish_MapsKindToLabel(ReportKind kind, string expected)
        => Assert.Equal(expected, Word("es", kind, LabelType.Header));
}