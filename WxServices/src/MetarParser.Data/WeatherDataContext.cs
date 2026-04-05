using MetarParser.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MetarParser.Data;

/// <summary>
/// Entity Framework Core database context for the WeatherData database.
/// Provides access to METAR and TAF tables.
/// </summary>
public sealed class WeatherDataContext : DbContext
{
    /// <summary>
    /// Initialises a new <see cref="WeatherDataContext"/> with the supplied options.
    /// Options are typically provided by the dependency-injection container or
    /// constructed directly with a connection string via
    /// <see cref="DbContextOptionsBuilder"/>.
    /// </summary>
    /// <param name="options">
    /// The options for this context, including the database provider and
    /// connection string.
    /// </param>
    public WeatherDataContext(DbContextOptions<WeatherDataContext> options)
        : base(options) { }

    /// <summary>
    /// The <c>Metars</c> table — one row per decoded METAR or SPECI report,
    /// containing all scalar fields and raw multi-value strings.
    /// </summary>
    public DbSet<MetarRecord> Metars => Set<MetarRecord>();

    /// <summary>
    /// The <c>MetarSkyConditions</c> table — one row per sky-condition layer,
    /// linked to its parent <see cref="MetarRecord"/> by foreign key.
    /// </summary>
    public DbSet<MetarSkyCondition> SkyConditions => Set<MetarSkyCondition>();

    /// <summary>
    /// The <c>MetarWeatherPhenomena</c> table — one row per weather phenomenon
    /// (present or recent), linked to its parent <see cref="MetarRecord"/>.
    /// </summary>
    public DbSet<MetarWeatherPhenomenon> WeatherPhenomena => Set<MetarWeatherPhenomenon>();

    /// <summary>
    /// The <c>MetarRunwayVisualRanges</c> table — one row per RVR group,
    /// linked to its parent <see cref="MetarRecord"/>.
    /// </summary>
    public DbSet<MetarRunwayVisualRange> RunwayVisualRanges => Set<MetarRunwayVisualRange>();

    // ── TAF tables ───────────────────────────────────────────────────────────

    /// <summary>The <c>Tafs</c> table — one row per decoded TAF report.</summary>
    public DbSet<TafRecord> Tafs => Set<TafRecord>();

    /// <summary>The <c>TafChangePeriods</c> table — base period (SortOrder 0) and BECMG/TEMPO/FM/PROB change groups.</summary>
    public DbSet<TafChangePeriodRecord> TafChangePeriods => Set<TafChangePeriodRecord>();

    /// <summary>The <c>TafChangePeriodSkyConditions</c> table — sky layers within a change period (including the base period).</summary>
    public DbSet<TafChangePeriodSky> TafChangePeriodSkyConditions => Set<TafChangePeriodSky>();

    /// <summary>The <c>TafChangePeriodWeatherPhenomena</c> table — weather phenomena within a change period (including the base period).</summary>
    public DbSet<TafChangePeriodWeather> TafChangePeriodWeatherPhenomena => Set<TafChangePeriodWeather>();

    /// <summary>The <c>Recipients</c> table — one row per weather report subscriber, holding profile, resolved location, and unit preferences.</summary>
    public DbSet<Recipient> Recipients => Set<Recipient>();

    /// <summary>The <c>GlobalSettings</c> table — single row (Id = 1) storing application-wide secrets (Claude API key, SMTP credentials).</summary>
    public DbSet<GlobalSettings> GlobalSettings => Set<GlobalSettings>();

    /// <summary>The <c>RecipientStates</c> table — one row per email recipient, tracking last send time and snapshot fingerprint.</summary>
    public DbSet<RecipientState> RecipientStates => Set<RecipientState>();

    /// <summary>
    /// The <c>GfsGrid</c> table — one row per GFS model grid point, forecast hour,
    /// and model-run time.  Holds the ingested NWP parameters used to augment
    /// the weather snapshot with medium-range gridded forecast data.
    /// </summary>
    public DbSet<GfsGridPoint> GfsGrid => Set<GfsGridPoint>();

    /// <summary>
    /// The <c>GfsModelRuns</c> table — one row per GFS model run, tracking whether
    /// ingestion is complete.  Consumers should only read from <c>GfsGrid</c> for
    /// runs whose <see cref="GfsModelRun.IsComplete"/> flag is <see langword="true"/>.
    /// </summary>
    public DbSet<GfsModelRun> GfsModelRuns => Set<GfsModelRun>();

    /// <summary>
    /// The <c>WxStations</c> table — one row per METAR reporting station, holding
    /// geographic metadata (name, lat, lon, elevation).  Rows are inserted on first
    /// encounter during a METAR fetch cycle and are never updated or deleted.
    /// </summary>
    public DbSet<WxStation> WxStations => Set<WxStation>();

    /// <summary>
    /// Configures the EF Core model: table names, column types, indexes,
    /// and relationships between entities.
    /// </summary>
    /// <param name="modelBuilder">
    /// The builder used to construct the model for this context.
    /// </param>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ── Metars ───────────────────────────────────────────────────────────

        modelBuilder.Entity<MetarRecord>(e =>
        {
            e.ToTable("Metars");
            e.HasKey(x => x.Id);

            e.Property(x => x.ReportType)  .HasMaxLength(6)  .IsRequired();
            e.Property(x => x.StationIcao) .HasMaxLength(4)  .IsFixedLength().IsRequired();
            e.Property(x => x.WindUnit)    .HasMaxLength(3);
            e.Property(x => x.AltimeterUnit).HasMaxLength(4);
            e.Property(x => x.RawSkyConditions)   .HasMaxLength(100);
            e.Property(x => x.RawWeatherPhenomena) .HasMaxLength(100);
            e.Property(x => x.RawRunwayVisualRange).HasMaxLength(100);
            e.Property(x => x.Remarks)    .HasMaxLength(500);
            e.Property(x => x.RawReport)  .HasMaxLength(300).IsRequired();
            e.Property(x => x.ReceivedUtc).IsRequired();

            e.Property(x => x.ObservationUtc).IsRequired();

            // Unique constraint: one record per station per observation time per type.
            // Prevents duplicate inserts of the same report.
            e.HasIndex(x => new { x.StationIcao, x.ObservationUtc, x.ReportType })
             .IsUnique()
             .HasDatabaseName("UX_Metars_Station_Time_Type");

            // Index to support common queries by station and by time.
            e.HasIndex(x => x.StationIcao)
             .HasDatabaseName("IX_Metars_StationIcao");
        });

        // ── MetarSkyConditions ───────────────────────────────────────────────

        modelBuilder.Entity<MetarSkyCondition>(e =>
        {
            e.ToTable("MetarSkyConditions");
            e.HasKey(x => x.Id);

            e.Property(x => x.Cover)     .HasMaxLength(3).IsRequired();
            e.Property(x => x.CloudType) .HasMaxLength(3);

            e.HasOne(x => x.Metar)
             .WithMany(m => m.SkyConditions)
             .HasForeignKey(x => x.MetarId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── MetarWeatherPhenomena ────────────────────────────────────────────

        modelBuilder.Entity<MetarWeatherPhenomenon>(e =>
        {
            e.ToTable("MetarWeatherPhenomena");
            e.HasKey(x => x.Id);

            e.Property(x => x.PhenomenonKind) .HasMaxLength(7).IsRequired();
            e.Property(x => x.Intensity)      .HasMaxLength(2).IsRequired();
            e.Property(x => x.Descriptor)     .HasMaxLength(2);
            e.Property(x => x.Precipitation)  .HasMaxLength(20);
            e.Property(x => x.Obscuration)    .HasMaxLength(2);
            e.Property(x => x.OtherPhenomenon).HasMaxLength(2);

            e.HasOne(x => x.Metar)
             .WithMany(m => m.WeatherPhenomena)
             .HasForeignKey(x => x.MetarId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── MetarRunwayVisualRanges ──────────────────────────────────────────

        modelBuilder.Entity<MetarRunwayVisualRange>(e =>
        {
            e.ToTable("MetarRunwayVisualRanges");
            e.HasKey(x => x.Id);

            e.Property(x => x.Runway).HasMaxLength(4).IsRequired();
            e.Property(x => x.Trend) .HasMaxLength(1);

            e.HasOne(x => x.Metar)
             .WithMany(m => m.RunwayVisualRanges)
             .HasForeignKey(x => x.MetarId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Tafs ─────────────────────────────────────────────────────────────

        modelBuilder.Entity<TafRecord>(e =>
        {
            e.ToTable("Tafs");
            e.HasKey(x => x.Id);

            e.Property(x => x.ReportType) .HasMaxLength(7) .IsRequired();
            e.Property(x => x.StationIcao).HasMaxLength(4) .IsFixedLength().IsRequired();
            e.Property(x => x.RawReport)  .HasColumnType("nvarchar(max)").IsRequired();
            e.Property(x => x.IssuanceUtc) .IsRequired();
            e.Property(x => x.ValidFromUtc).IsRequired();
            e.Property(x => x.ValidToUtc)  .IsRequired();
            e.Property(x => x.ReceivedUtc) .IsRequired();

            // One TAF per station per issuance time per type.
            e.HasIndex(x => new { x.StationIcao, x.IssuanceUtc, x.ReportType })
             .IsUnique()
             .HasDatabaseName("UX_Tafs_Station_Issuance_Type");

            e.HasIndex(x => x.StationIcao)
             .HasDatabaseName("IX_Tafs_StationIcao");
        });

        // ── TafChangePeriods ─────────────────────────────────────────────────

        modelBuilder.Entity<TafChangePeriodRecord>(e =>
        {
            e.ToTable("TafChangePeriods");
            e.HasKey(x => x.Id);

            e.Property(x => x.ChangeType)         .HasMaxLength(12).IsRequired();
            e.Property(x => x.WindUnit)            .HasMaxLength(3);
            e.Property(x => x.RawSkyConditions)    .HasMaxLength(150);
            e.Property(x => x.RawWeather)          .HasMaxLength(100);

            e.HasOne(x => x.Taf)
             .WithMany(t => t.ChangePeriods)
             .HasForeignKey(x => x.TafId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── TafChangePeriodSkyConditions ─────────────────────────────────────

        modelBuilder.Entity<TafChangePeriodSky>(e =>
        {
            e.ToTable("TafChangePeriodSkyConditions");
            e.HasKey(x => x.Id);

            e.Property(x => x.Cover)    .HasMaxLength(3).IsRequired();
            e.Property(x => x.CloudType).HasMaxLength(3);

            e.HasOne(x => x.ChangePeriod)
             .WithMany(p => p.SkyConditions)
             .HasForeignKey(x => x.TafChangePeriodId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── TafChangePeriodWeatherPhenomena ──────────────────────────────────

        modelBuilder.Entity<TafChangePeriodWeather>(e =>
        {
            e.ToTable("TafChangePeriodWeatherPhenomena");
            e.HasKey(x => x.Id);

            e.Property(x => x.Intensity)      .HasMaxLength(2) .IsRequired();
            e.Property(x => x.Descriptor)     .HasMaxLength(2);
            e.Property(x => x.Precipitation)  .HasMaxLength(20);
            e.Property(x => x.Obscuration)    .HasMaxLength(2);
            e.Property(x => x.OtherPhenomenon).HasMaxLength(2);

            e.HasOne(x => x.ChangePeriod)
             .WithMany(p => p.WeatherPhenomena)
             .HasForeignKey(x => x.TafChangePeriodId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Recipients ───────────────────────────────────────────────────────────

        modelBuilder.Entity<Recipient>(e =>
        {
            e.ToTable("Recipients");
            e.HasKey(x => x.Id);

            e.Property(x => x.RecipientId)       .HasMaxLength(100).IsRequired();
            e.Property(x => x.Email)              .HasMaxLength(200).IsRequired();
            e.Property(x => x.Name)               .HasMaxLength(200).IsRequired();
            e.Property(x => x.Language)           .HasMaxLength(50);
            e.Property(x => x.Timezone)           .HasMaxLength(100).IsRequired();
            e.Property(x => x.ScheduledSendHours) .HasMaxLength(50);
            e.Property(x => x.Address)            .HasMaxLength(500);
            e.Property(x => x.LocalityName)       .HasMaxLength(200);
            e.Property(x => x.MetarIcao)          .HasMaxLength(100);
            e.Property(x => x.TafIcao)            .HasMaxLength(10);
            e.Property(x => x.TempUnit)           .HasMaxLength(10).IsRequired();
            e.Property(x => x.PressureUnit)       .HasMaxLength(10).IsRequired();
            e.Property(x => x.WindSpeedUnit)      .HasMaxLength(10).IsRequired();

            e.HasIndex(x => x.RecipientId)
             .IsUnique()
             .HasDatabaseName("UX_Recipients_RecipientId");
        });

        // ── GlobalSettings ────────────────────────────────────────────────────────

        modelBuilder.Entity<GlobalSettings>(e =>
        {
            e.ToTable("GlobalSettings");
            e.HasKey(x => x.Id);

            e.Property(x => x.ClaudeApiKey)    .HasMaxLength(500);
            e.Property(x => x.SmtpUsername)    .HasMaxLength(200);
            e.Property(x => x.SmtpPassword)    .HasMaxLength(200);
            e.Property(x => x.SmtpFromAddress) .HasMaxLength(200);
        });

        // ── RecipientStates ──────────────────────────────────────────────────

        modelBuilder.Entity<RecipientState>(e =>
        {
            e.ToTable("RecipientStates");
            e.HasKey(x => x.Id);

            e.Property(x => x.RecipientId)            .HasMaxLength(100).IsRequired();
            e.Property(x => x.LastSnapshotFingerprint).HasMaxLength(200);
            e.Property(x => x.LastMetarIcao)          .HasMaxLength(4).IsFixedLength();

            e.HasIndex(x => x.RecipientId)
             .IsUnique()
             .HasDatabaseName("UX_RecipientStates_RecipientId");
        });

        // ── GfsModelRuns ─────────────────────────────────────────────────────

        modelBuilder.Entity<GfsModelRun>(e =>
        {
            e.ToTable("GfsModelRuns");
            e.HasKey(x => x.ModelRunUtc);
            e.Property(x => x.IsComplete).IsRequired();
        });

        // ── WxStations ───────────────────────────────────────────────────────

        modelBuilder.Entity<WxStation>(e =>
        {
            e.ToTable("WxStations");
            e.HasKey(x => x.IcaoId);

            e.Property(x => x.IcaoId)     .HasMaxLength(4).IsFixedLength().IsRequired();
            e.Property(x => x.Name)       .HasMaxLength(100);
            e.Property(x => x.Lat);
            e.Property(x => x.Lon);
            e.Property(x => x.ElevationFt);
        });

        // ── GfsGrid ──────────────────────────────────────────────────────────

        modelBuilder.Entity<GfsGridPoint>(e =>
        {
            e.ToTable("GfsGrid");
            e.HasKey(x => x.Id);

            e.Property(x => x.ModelRunUtc) .IsRequired();
            e.Property(x => x.ForecastHour).IsRequired();
            e.Property(x => x.Lat)         .IsRequired();
            e.Property(x => x.Lon)         .IsRequired();

            // Prevent duplicate ingestion of the same grid point / run / hour.
            e.HasIndex(x => new { x.ModelRunUtc, x.ForecastHour, x.Lat, x.Lon })
             .IsUnique()
             .HasDatabaseName("UX_GfsGrid_Run_Hour_LatLon");

            // Support fast "all points for run X at forecast hour Y" queries.
            e.HasIndex(x => new { x.ModelRunUtc, x.ForecastHour })
             .HasDatabaseName("IX_GfsGrid_Run_Hour");
        });
    }
}
