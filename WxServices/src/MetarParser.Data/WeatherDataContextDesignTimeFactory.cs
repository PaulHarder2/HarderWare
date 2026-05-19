using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MetarParser.Data;

/// <summary>
/// Design-time <see cref="WeatherDataContext"/> factory used by EF Core tooling
/// (<c>dotnet ef migrations add</c>, <c>dotnet ef migrations script</c>, etc.).
/// The tooling needs a configured <see cref="DbContext"/> to introspect the
/// model and emit migration code, but it does not need a real connection — no
/// queries are issued, only the SQL Server dialect is required so the
/// generated DDL targets the correct provider.
/// </summary>
internal sealed class WeatherDataContextDesignTimeFactory
    : IDesignTimeDbContextFactory<WeatherDataContext>
{
    public WeatherDataContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<WeatherDataContext>()
            .UseSqlServer("Server=(localdb)\\design-time;Database=WeatherData;Integrated Security=true;TrustServerCertificate=true;")
            .Options;
        return new WeatherDataContext(options);
    }
}
