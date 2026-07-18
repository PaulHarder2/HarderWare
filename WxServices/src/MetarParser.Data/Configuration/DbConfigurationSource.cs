using Microsoft.Extensions.Configuration;

namespace MetarParser.Data.Configuration;

/// <summary>
/// <see cref="IConfigurationSource"/> for <see cref="DbConfigurationProvider"/>.
/// Appended to a configuration builder by
/// <see cref="DbConfigurationExtensions.AddDatabaseConfig"/>.
/// </summary>
internal sealed class DbConfigurationSource : IConfigurationSource
{
    private readonly Func<WeatherDataContext>? _contextFactory;

    public DbConfigurationSource(Func<WeatherDataContext>? contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new DbConfigurationProvider(_contextFactory);
}