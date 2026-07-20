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

    [Fact]
    public void Build_PlansFourContainerFiles_PlusWxManager()
    {
        var files = LocalFilesPlan.Build(Options(), "pw", readExample: _ => ContainerExample);

        Assert.Equal(5, files.Count);
        foreach (var svc in new[] { "wxparser", "wxreport", "wxmonitor", "wxvis" })
            Assert.Contains(files, f => f.Path == Path.Combine(@"C:\svc", svc, "appsettings.local.json"));
        Assert.Contains(files, f => f.Path == Path.Combine(@"C:\Root", "appsettings.local.json"));
    }

    [Fact]
    public void Build_ContainerFile_UsesTargetDbLoginPassword()
    {
        var files = LocalFilesPlan.Build(Options(), "pw", _ => ContainerExample);
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
        var files = LocalFilesPlan.Build(Options(), "pw", _ => ContainerExample);
        var wxmgr = files.Single(f => f.Path == Path.Combine(@"C:\Root", "appsettings.local.json"));

        using var doc = JsonDocument.Parse(wxmgr.Content);
        var conn = new SqlConnectionStringBuilder(
            doc.RootElement.GetProperty("ConnectionStrings").GetProperty("WeatherData").GetString()!);
        Assert.Equal(@".\SQLEXPRESS", conn.DataSource);
        Assert.Equal("WeatherDataTest", conn.InitialCatalog);
        Assert.True(conn.IntegratedSecurity);
        Assert.True(string.IsNullOrEmpty(conn.Password));
    }
}