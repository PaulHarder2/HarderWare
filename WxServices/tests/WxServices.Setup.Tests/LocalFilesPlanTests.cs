using System.IO;
using System.Linq;
using System.Text.Json;

using Microsoft.Data.SqlClient;

using WxServices.Setup;

using Xunit;

namespace WxServices.Setup.Tests;

/// <summary>
/// WX-314 AC-5, test-first: planning the five per-environment files (four container + WxManager) —
/// correct paths, and each container file's connection string rebuilt for the target DB/login/password
/// while the WxManager file stays native Trusted. Pure — template reads are injected, no disk touched.
/// </summary>
public class LocalFilesPlanTests
{
    private const string ContainerExample = """
        {
          "ConnectionStrings": {
            "WeatherData": "Server=host.docker.internal,1433;Database=WeatherData;User Id=wxservices;Password=<WXSERVICES_SQL_PASSWORD>;TrustServerCertificate=True;"
          },
          "Telemetry": { "Enabled": true }
        }
        """;

    private static SetupOptions Options() =>
        new("full", @"C:\Root", @"C:\svc", "WeatherDataTest", "wxservicestest", @".\SQLEXPRESS");

    /// <summary>The fresh-box path — no WxManager appsettings.local.json on disk yet.</summary>
    private static readonly Func<string, string?> NoExistingFile = _ => null;

    private static readonly string WxManagerPath = Path.Combine(@"C:\Root", "appsettings.local.json");

    [Fact]
    public void Build_PlansFourContainerFiles_PlusWxManager()
    {
        var files = LocalFilesPlan.Build(
            Options(), "pw", readExample: _ => ContainerExample, readExistingWxManager: NoExistingFile);

        Assert.Equal(5, files.Count);
        foreach (var svc in new[] { "wxparser", "wxreport", "wxmonitor", "wxvis" })
            Assert.Contains(files, f => f.Path == Path.Combine(@"C:\svc", svc, "appsettings.local.json"));
        Assert.Contains(files, f => f.Path == Path.Combine(@"C:\Root", "appsettings.local.json"));
    }

    [Fact]
    public void Build_ContainerFile_UsesTargetDbLoginPassword()
    {
        var files = LocalFilesPlan.Build(Options(), "pw", _ => ContainerExample, NoExistingFile);
        var wxparser = files.Single(f => f.Path == Path.Combine(@"C:\svc", "wxparser", "appsettings.local.json"));

        using var doc = JsonDocument.Parse(wxparser.Content);
        var conn = new SqlConnectionStringBuilder(
            doc.RootElement.GetProperty("ConnectionStrings").GetProperty("WeatherData").GetString()!);
        Assert.Equal("host.docker.internal,1433", conn.DataSource);
        Assert.Equal("WeatherDataTest", conn.InitialCatalog);
        Assert.Equal("wxservicestest", conn.UserID);
        Assert.Equal("pw", conn.Password);
        Assert.DoesNotContain("<WXSERVICES_SQL_PASSWORD>", wxparser.Content);
    }

    [Fact]
    public void Build_WxManagerFile_IsTrusted_NoPassword()
    {
        var files = LocalFilesPlan.Build(Options(), "pw", _ => ContainerExample, NoExistingFile);
        var wxmgr = files.Single(f => f.Path == Path.Combine(@"C:\Root", "appsettings.local.json"));

        using var doc = JsonDocument.Parse(wxmgr.Content);
        var conn = new SqlConnectionStringBuilder(
            doc.RootElement.GetProperty("ConnectionStrings").GetProperty("WeatherData").GetString()!);
        Assert.Equal(@".\SQLEXPRESS", conn.DataSource);
        Assert.Equal("WeatherDataTest", conn.InitialCatalog);
        Assert.True(conn.IntegratedSecurity);
        Assert.True(string.IsNullOrEmpty(conn.Password));
    }

    // ---- WX-326: a re-run merges into the operator's file, it does not rebuild it -------------

    [Fact]
    public void Build_WxManagerFile_MergesIntoExisting_PreservingOperatorKeys()
    {
        const string existing = """
            {
              "ConnectionStrings": { "WeatherData": "Server=OLD;Database=Old;Trusted_Connection=True;" },
              "Fetch": { "HomeIcao": "KAUS", "RegionSouth": 22 },
              "Claude": { "Model": "claude-sonnet-4-6" },
              "WxVis": { "MapExtent": "conus" }
            }
            """;

        var files = LocalFilesPlan.Build(Options(), "pw", _ => ContainerExample, _ => existing);
        var wxmgr = files.Single(f => f.Path == WxManagerPath);

        using var doc = JsonDocument.Parse(wxmgr.Content);
        var root = doc.RootElement;

        // The connection string is the one thing setup owns, and it was updated...
        var conn = new SqlConnectionStringBuilder(
            root.GetProperty("ConnectionStrings").GetProperty("WeatherData").GetString()!);
        Assert.Equal(@".\SQLEXPRESS", conn.DataSource);
        Assert.Equal("WeatherDataTest", conn.InitialCatalog);
        Assert.DoesNotContain("Server=OLD", wxmgr.Content);

        // ...and every operator-owned key survived it.
        Assert.Equal("KAUS", root.GetProperty("Fetch").GetProperty("HomeIcao").GetString());
        Assert.Equal(22, root.GetProperty("Fetch").GetProperty("RegionSouth").GetInt32());
        Assert.Equal("claude-sonnet-4-6", root.GetProperty("Claude").GetProperty("Model").GetString());
        Assert.Equal("conus", root.GetProperty("WxVis").GetProperty("MapExtent").GetString());
    }

    [Fact]
    public void Build_WxManagerFile_ReadsTheWxManagerPath_NotSomeOtherFile()
    {
        // Guards the wiring, not the merge: if Build asked the reader for the wrong path it would
        // merge into the wrong file's content (or none), and every other assertion here would
        // still pass.
        var asked = new List<string>();

        LocalFilesPlan.Build(Options(), "pw", _ => ContainerExample, p => { asked.Add(p); return null; });

        Assert.Equal(new[] { WxManagerPath }, asked);
    }

    [Theory]
    [InlineData("{ this is not json")]          // malformed
    [InlineData("[1, 2, 3]")]                   // valid JSON, but not an object
    [InlineData("""{ "ConnectionStrings": "not-an-object" }""")]
    public void Build_UnparseableExistingWxManagerFile_ThrowsNamingThePath(string existing)
    {
        var ex = Assert.Throws<SetupException>(
            () => LocalFilesPlan.Build(Options(), "pw", _ => ContainerExample, _ => existing));

        Assert.Contains(WxManagerPath, ex.Message);
        Assert.Contains("will not overwrite", ex.Message);
    }

    [Fact]
    public void Build_PreservesNonAsciiAndPunctuationVerbatim_NotEscaped()
    {
        // The operator's file must come back looking like the operator's file. The default encoder
        // rewrites these to ø / & etc — semantically identical, visibly not theirs.
        // Built from char codes rather than literals so this source file stays pure ASCII: a literal
        // "København" here would depend on the file's own encoding surviving every tool that touches
        // it, and the one input this test exists to exercise is exactly the one such a tool would
        // mangle. (The first cut of this test claimed that property in a comment while using raw
        // literals — caught by code review.)
        var danish = $"K{(char)0xF8}benhavn {(char)0xC6}r{(char)0xF8} {(char)0xC5}rhus";
        var punctuation = "a<b>c&d+e'f";
        var existing = $$"""
            {
              "ConnectionStrings": { "WeatherData": "Server=OLD;" },
              "Fetch": { "Locality": "{{danish}}" },
              "WxVis": { "Note": "{{punctuation}}" }
            }
            """;

        var content = LocalFilesPlan
            .Build(Options(), "pw", _ => ContainerExample, _ => existing)
            .Single(f => f.Path == WxManagerPath).Content;

        Assert.Contains(danish, content);          // raw text, not the re-parsed value
        Assert.Contains(punctuation, content);
        Assert.DoesNotContain("\\u00", content);
    }

    [Theory]
    [InlineData("connectionStrings", "weatherData")]   // operator used camelCase throughout
    [InlineData("ConnectionStrings", "weatherdata")]   // ...or only on the inner key
    [InlineData("CONNECTIONSTRINGS", "WEATHERDATA")]
    public void Build_ExistingKeysInAnyCase_AreUpdatedInPlace_NotDuplicated(string outer, string inner)
    {
        // JSON is case-sensitive; the .NET config binder is not. So a file spelled "connectionStrings"
        // works today, and an ordinal lookup would miss it and ADD a second key differing only in case
        // — which JsonConfigurationFileParser folds and then REJECTS, so setup would report success and
        // WxManager would refuse to start. Found by code review; reproduced before fixing.
        var existing = $$"""
            { "{{outer}}": { "{{inner}}": "Server=OLD;" }, "Fetch": { "HomeIcao": "KAUS" } }
            """;

        var content = LocalFilesPlan
            .Build(Options(), "pw", _ => ContainerExample, _ => existing)
            .Single(f => f.Path == WxManagerPath).Content;

        using var doc = JsonDocument.Parse(content);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        // Exactly one ConnectionStrings-ish member, spelled as the operator spelled it.
        Assert.Single(names, n => string.Equals(n, "ConnectionStrings", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(outer, names);

        var connections = doc.RootElement.GetProperty(outer);
        var innerNames = connections.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Single(innerNames, n => string.Equals(n, "WeatherData", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            "WeatherDataTest",
            new SqlConnectionStringBuilder(connections.GetProperty(inner).GetString()!).InitialCatalog);
        Assert.Equal("KAUS", doc.RootElement.GetProperty("Fetch").GetProperty("HomeIcao").GetString());
    }

    [Fact]
    public void Build_ExistingFileAlreadyHasCaseVariantDuplicates_ThrowsRatherThanGuessing()
    {
        // Already unloadable before we touch it. Say so plainly instead of silently picking one and
        // writing a file that still will not load.
        var ex = Assert.Throws<SetupException>(
            () => LocalFilesPlan.Build(
                Options(), "pw", _ => ContainerExample,
                _ => """{ "ConnectionStrings": { "WeatherData": "a" }, "connectionStrings": { "x": "b" } }"""));

        Assert.Contains(WxManagerPath, ex.Message);
        Assert.Contains("differing only in case", ex.Message);
    }

    [Fact]
    public void Build_NonJsonSyntaxFailure_OmitsTheCommentsHint()
    {
        // Four causes reach the wrap; the hint explains one. An operator whose file has no comment
        // and no trailing comma must not be sent hunting for one.
        var ex = Assert.Throws<SetupException>(
            () => LocalFilesPlan.Build(
                Options(), "pw", _ => ContainerExample, _ => """{ "ConnectionStrings": "not-an-object" }"""));

        Assert.Contains(WxManagerPath, ex.Message);          // the path is unconditional...
        Assert.DoesNotContain("comments", ex.Message);        // ...the remedy note is not
        Assert.DoesNotContain("trailing commas", ex.Message);
    }

    [Fact]
    public void Build_UnreadableExistingFile_StillNamesThePath()
    {
        // The reader throwing (locked file, permission denied) must not escape unwrapped — an
        // operator staring at a generic IO failure has no idea which file it was.
        var ex = Assert.Throws<SetupException>(
            () => LocalFilesPlan.Build(
                Options(), "pw", _ => ContainerExample,
                _ => throw new UnauthorizedAccessException("Access to the path is denied.")));

        Assert.Contains(WxManagerPath, ex.Message);
        Assert.IsType<UnauthorizedAccessException>(ex.InnerException);
    }

    [Fact]
    public void Build_ExistingFileWithCommentsOrTrailingCommas_ThrowsAndExplainsWhy()
    {
        // Not an oversight — a decision, pinned here so it cannot be "fixed" into data loss.
        // The JSON configuration provider the services read this file with accepts // comments and
        // trailing commas (verified 2026-07-26), so this content runs fine. We still refuse it,
        // because merging means re-serializing and System.Text.Json cannot round-trip a comment:
        // parsing leniently would silently delete the operator's annotations. The message has to
        // say so, or the operator reads "invalid JSON" about a file that demonstrably works.
        const string lenient = """
            {
              // the box this was cloned from
              "ConnectionStrings": { "WeatherData": "Server=OLD;" },
              "Fetch": { "HomeIcao": "KAUS" },
            }
            """;

        var ex = Assert.Throws<SetupException>(
            () => LocalFilesPlan.Build(Options(), "pw", _ => ContainerExample, _ => lenient));

        Assert.Contains(WxManagerPath, ex.Message);
        Assert.Contains("comments", ex.Message);
        Assert.Contains("trailing commas", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_NoExistingWxManagerFile_WritesConnectionStringOnly(string? existing)
    {
        var files = LocalFilesPlan.Build(Options(), "pw", _ => ContainerExample, _ => existing);
        var wxmgr = files.Single(f => f.Path == WxManagerPath);

        using var doc = JsonDocument.Parse(wxmgr.Content);
        var root = doc.RootElement;
        Assert.Equal(
            @".\SQLEXPRESS",
            new SqlConnectionStringBuilder(
                root.GetProperty("ConnectionStrings").GetProperty("WeatherData").GetString()!).DataSource);
        Assert.False(root.TryGetProperty("Fetch", out _));   // foundational fields are DB-seeded (WX-314)
        Assert.False(root.TryGetProperty("WxVis", out _));
    }

    [Fact]
    public void Build_OnlyTheWxManagerFileIsWrittenAtomically()
    {
        // The container files are single-file Docker bind mounts: replacing the directory entry swaps
        // the inode out from under every running container, which then reads the old unlinked file
        // forever (restart: unless-stopped). They MUST be truncated in place. The WxManager file has no
        // bind mount and is now the only copy of the operator's keys, so it MUST be replaced atomically.
        // Opposite requirements — hence a per-file flag, and hence this test. (WX-326, code review.)
        var files = LocalFilesPlan.Build(Options(), "pw", _ => ContainerExample, NoExistingFile);

        var atomic = files.Where(f => f.AtomicReplace).Select(f => f.Path).ToList();

        Assert.Equal(new[] { WxManagerPath }, atomic);
        Assert.All(
            files.Where(f => f.Path != WxManagerPath),
            f => Assert.False(f.AtomicReplace, $"{f.Path} is a bind-mount target and must not be replaced"));
    }








    [Fact]
    public void Build_UnreadableExistingFile_StillFailsBeforeAnyMutation()
    {
        // Plan time is different from refresh time: here nothing has been provisioned yet, so an
        // unreadable file SHOULD stop the run rather than fall back.
        var ex = Assert.Throws<SetupException>(
            () => LocalFilesPlan.Build(
                Options(), "pw", _ => ContainerExample,
                _ => throw new UnauthorizedAccessException("Access to the path is denied.")));

        Assert.Contains(WxManagerPath, ex.Message);
    }
}