using System.Collections.Generic;
using System.Text.Json;

using Microsoft.Data.SqlClient;

using WxServices.Setup;

using Xunit;

namespace WxServices.Setup.Tests;

/// <summary>
/// WX-314, written test-first (see docs/test-procedures/WX-314.md §0). These pin the pure
/// generators the setup script uses — connection strings, per-environment local.json, and the
/// foundational Config seed rows — before the code exists. They are RED until the generators
/// are implemented (green phase).
/// </summary>
public class ConnectionStringTests
{
    [Fact]
    public void BuildContainer_IsHostDockerInternal_WithSqlAuth()
    {
        var b = new SqlConnectionStringBuilder(ConnectionStrings.BuildContainer("WeatherData", "wxservices", "pw"));

        Assert.Equal("host.docker.internal,1433", b.DataSource);
        Assert.Equal("WeatherData", b.InitialCatalog);
        Assert.Equal("wxservices", b.UserID);
        Assert.Equal("pw", b.Password);
        Assert.True(b.TrustServerCertificate);
        Assert.False(b.IntegratedSecurity);   // SQL auth, not Trusted
    }

    [Theory]
    [InlineData("WeatherDataTest", "wxservicestest", "s3cret")]
    public void BuildContainer_SubstitutesParameters(string db, string login, string pw)
    {
        var b = new SqlConnectionStringBuilder(ConnectionStrings.BuildContainer(db, login, pw));

        Assert.Equal("host.docker.internal,1433", b.DataSource);
        Assert.Equal(db, b.InitialCatalog);
        Assert.Equal(login, b.UserID);
        Assert.Equal(pw, b.Password);
    }

    [Fact]
    public void BuildContainer_EscapesPasswordWithSpecialChars()
    {
        // The whole point of SqlConnectionStringBuilder: a password with ; = " ' must round-trip,
        // not split (or inject into) the connection string.
        const string tricky = "pa;ss=w\"or'd";

        var b = new SqlConnectionStringBuilder(ConnectionStrings.BuildContainer("WeatherData", "wxservices", tricky));

        Assert.Equal(tricky, b.Password);
    }

    [Fact]
    public void BuildWxManager_IsTrusted_NoPassword()
    {
        var b = new SqlConnectionStringBuilder(ConnectionStrings.BuildWxManager(@".\SQLEXPRESS", "WeatherData"));

        Assert.Equal(@".\SQLEXPRESS", b.DataSource);
        Assert.Equal("WeatherData", b.InitialCatalog);
        Assert.True(b.IntegratedSecurity);
        Assert.True(string.IsNullOrEmpty(b.Password));
    }
}

public class LocalJsonGeneratorTests
{
    // A minimal stand-in for services/<svc>/appsettings.local.json.example — carries the
    // container connection string placeholder plus the docs + extras the generator must preserve.
    private const string ContainerExample = """
        {
          "_README": ["template docs"],
          "ConnectionStrings": {
            "_README": ["connection docs"],
            "WeatherData": "Server=host.docker.internal,1433;Database=WeatherData;User Id=wxservices;Password=<WXSERVICES_SQL_PASSWORD>;TrustServerCertificate=True;"
          },
          "Telemetry": { "Enabled": true, "OtlpEndpoint": "http://host.docker.internal:4318/v1/metrics" },
          "Gfs": { "Wgrib2Path": "/usr/local/bin/wgrib2" }
        }
        """;

    [Fact]
    public void BuildContainerLocalJson_ReplacesConnectionString_PreservesEverythingElse()
    {
        var built = "Server=host.docker.internal,1433;Database=WeatherDataTest;User Id=wxservicestest;Password=pw;TrustServerCertificate=True;";

        var json = LocalJsonGenerator.BuildContainerLocalJson(ContainerExample, built);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(built, root.GetProperty("ConnectionStrings").GetProperty("WeatherData").GetString());
        Assert.DoesNotContain("<WXSERVICES_SQL_PASSWORD>", json);          // placeholder gone
        Assert.True(root.GetProperty("Telemetry").GetProperty("Enabled").GetBoolean());   // preserved
        Assert.Equal("/usr/local/bin/wgrib2", root.GetProperty("Gfs").GetProperty("Wgrib2Path").GetString());
        Assert.True(root.GetProperty("ConnectionStrings").TryGetProperty("_README", out _));  // docs preserved
    }

    [Fact]
    public void BuildWxManagerLocalJson_HasOnlyConnectionString_NoFoundationalFields()
    {
        var cs = @"Server=.\SQLEXPRESS;Database=WeatherData;Trusted_Connection=True;TrustServerCertificate=True;";

        var json = LocalJsonGenerator.BuildWxManagerLocalJson(cs);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(cs, root.GetProperty("ConnectionStrings").GetProperty("WeatherData").GetString());
        Assert.False(root.TryGetProperty("Fetch", out _));    // foundational fields are DB-seeded now, not file
        Assert.False(root.TryGetProperty("WxVis", out _));
    }
}

public class ConfigSeedTests
{
    [Fact]
    public void BuildFoundationalSeedRows_EmitsFoundationalKeys_AndNoBootstrapKeys()
    {
        var inputs = new FoundationalInputs(
            HomeIcao: "KAUS", HomeLatitude: 30.2, HomeLongitude: -97.7, BoundingBoxDegrees: 9,
            RegionSouth: 22, RegionNorth: 50, RegionWest: -126, RegionEast: -65, MapExtent: "conus");

        var rows = ConfigSeed.BuildFoundationalSeedRows(inputs);

        var keys = new HashSet<string>();
        foreach (var r in rows) keys.Add(r.Key);

        Assert.Contains("Fetch:HomeIcao", keys);
        Assert.Contains("Fetch:HomeLatitude", keys);
        Assert.Contains("Fetch:HomeLongitude", keys);
        Assert.Contains("Fetch:BoundingBoxDegrees", keys);
        Assert.Contains("Fetch:RegionSouth", keys);
        Assert.Contains("Fetch:RegionNorth", keys);
        Assert.Contains("Fetch:RegionWest", keys);
        Assert.Contains("Fetch:RegionEast", keys);
        Assert.Contains("WxVis:MapExtent", keys);

        Assert.Equal("KAUS", ValueOf(rows, "Fetch:HomeIcao"));
        Assert.Equal("conus", ValueOf(rows, "WxVis:MapExtent"));

        foreach (var k in keys)
        {
            Assert.False(k.StartsWith("ConnectionStrings:"));
            Assert.False(k.StartsWith("Database:StartupRetry:"));
            Assert.False(k.StartsWith("Telemetry:"));
            Assert.NotEqual("Claude:TimeoutSeconds", k);
        }
    }

    private static string? ValueOf(IReadOnlyList<KeyValuePair<string, string?>> rows, string key)
    {
        foreach (var r in rows)
            if (r.Key == key)
                return r.Value;
        return null;
    }
}