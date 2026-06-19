using System.Runtime.CompilerServices;

using Xunit;

namespace WxReport.Tests.Golden;

/// <summary>
/// WX-171 no-regression gate. <see cref="Verify"/> renders every
/// <see cref="RendererGoldenCorpus"/> case with the current renderer and asserts
/// it matches the committed golden fixture byte-for-byte — so the DB-template
/// rewire cannot change a single rendered byte without a golden diff that a
/// reviewer must consciously approve. Intended changes (the es "lluvia helada"
/// order; en wording refinements from the manual DB review) are landed by
/// re-recording and committing the golden diff in the same PR, each justified.
///
/// <para>
/// Capture/refresh the goldens (run once before the rewire, then again per
/// intended change) by setting the gate env var:
/// <code>WX171_RECORD=1 dotnet test --filter FullyQualifiedName~RendererGoldenTests.Record</code>
/// The <see cref="Record"/> fact no-ops unless that variable is set, so CI never
/// rewrites the goldens it is meant to check.
/// </para>
/// </summary>
public class RendererGoldenTests
{
    private const string RecordEnvVar = "WX171_RECORD";

    public static IEnumerable<object[]> CaseNames =>
        RendererGoldenCorpus.All().Select(c => new object[] { c.Name });

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Verify(string name)
    {
        var expectedPath = FixturePath(name);
        if (!File.Exists(expectedPath))
            Assert.Fail($"Golden fixture missing: {Path.GetFileName(expectedPath)}. " +
                        $"Capture it with: {RecordEnvVar}=1 dotnet test --filter FullyQualifiedName~RendererGoldenTests.Record");

        var actual = RendererGoldenCorpus.All().Single(c => c.Name == name).Content;
        var expected = File.ReadAllText(expectedPath);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Record()
    {
        if (Environment.GetEnvironmentVariable(RecordEnvVar) != "1")
            return;  // gate: never rewrite goldens unless explicitly recording

        var dir = FixtureDir();
        Directory.CreateDirectory(dir);
        var written = 0;
        foreach (var c in RendererGoldenCorpus.All())
        {
            File.WriteAllText(Path.Combine(dir, $"{c.Name}.golden.html"), c.Content);
            written++;
        }
        Assert.True(written > 0, "corpus produced no cases to record");
    }

    private static string FixturePath(string name) => Path.Combine(FixtureDir(), $"{name}.golden.html");

    private static string FixtureDir([CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "Fixtures");
}