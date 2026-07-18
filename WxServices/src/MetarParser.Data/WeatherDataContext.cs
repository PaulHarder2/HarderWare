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

    /// <summary>The <c>Languages</c> table — ISO 639-1 registry; enabled rows are the supported languages a recipient may be assigned (WX-166).</summary>
    public DbSet<Language> Languages => Set<Language>();

    /// <summary>The <c>LanguageTemplates</c> table — token-keyed localized report strings; supersedes the interim hard-coded <c>ReportVocabulary</c> (WX-167).</summary>
    public DbSet<LanguageTemplate> LanguageTemplates => Set<LanguageTemplate>();

    /// <summary>The <c>PromptGlossaryTokens</c> table — language-neutral concept tokens whose approved phrase is injected into the reconciler prompt as an anchoring glossary (WX-238).</summary>
    public DbSet<PromptGlossaryToken> PromptGlossaryTokens => Set<PromptGlossaryToken>();

    /// <summary>The <c>Localities</c> table — one row per curated locality grouping co-located recipients for batched report generation (WX-123).</summary>
    public DbSet<Locality> Localities => Set<Locality>();

    /// <summary>The <c>GlobalSettings</c> table — single row (Id = 1) storing application-wide secrets (Claude API key, SMTP credentials).</summary>
    public DbSet<GlobalSettings> GlobalSettings => Set<GlobalSettings>();

    /// <summary>The <c>Config</c> table — key/value application configuration (WX-313), layered into <c>IConfiguration</c> by a DB-backed provider; the runtime single source of truth for app config (WX-307).</summary>
    public DbSet<Config> Config => Set<Config>();

    /// <summary>The <c>QaRerunRequests</c> table — one row per language tracking a service-side "Rerun QA" judge-package regeneration (WX-235).</summary>
    public DbSet<QaRerunRequest> QaRerunRequests => Set<QaRerunRequest>();

    /// <summary>The <c>RecipientStates</c> table — one row per email recipient, tracking last send time and snapshot fingerprint.</summary>
    public DbSet<RecipientState> RecipientStates => Set<RecipientState>();

    /// <summary>The <c>LocalityStates</c> table — one row per locality, holding the shared reconciliation baseline and send cadence (WX-130).</summary>
    public DbSet<LocalityState> LocalityStates => Set<LocalityState>();

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
    /// geographic metadata (name, municipality, lat, lon, elevation).  Rows are
    /// inserted on first encounter during a METAR fetch cycle and may be updated
    /// by the OurAirports importer.
    /// </summary>
    public DbSet<WxStation> WxStations => Set<WxStation>();

    /// <summary>
    /// The <c>ForecastSnapshots</c> table — one row per committed forecast
    /// snapshot per station.  The persisted anchor that later report cycles
    /// diff against to decide whether the forecast has been invalidated.
    /// Introduced under WX-76 for the WX-47 rearchitecture; populated by WX-77,
    /// persisted by WX-78, and reasoned over by WX-79.
    /// </summary>
    public DbSet<ForecastSnapshot> ForecastSnapshots => Set<ForecastSnapshot>();

    /// <summary>
    /// The <c>CommittedSends</c> table — one row per report-send to a single
    /// recipient, anchored to a <see cref="ForecastSnapshot"/> and carrying
    /// the Claude reasoning trace and rendered email body.  Introduced under
    /// WX-78; the trace column is reserved for WX-79.
    /// </summary>
    public DbSet<CommittedSend> CommittedSends => Set<CommittedSend>();

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

            e.Property(x => x.ReportType).HasMaxLength(6).IsRequired();
            e.Property(x => x.StationIcao).HasMaxLength(4).IsFixedLength().IsRequired();
            e.Property(x => x.WindUnit).HasMaxLength(3);
            e.Property(x => x.AltimeterUnit).HasMaxLength(4);
            e.Property(x => x.RawSkyConditions).HasMaxLength(100);
            e.Property(x => x.RawWeatherPhenomena).HasMaxLength(100);
            e.Property(x => x.RawRunwayVisualRange).HasMaxLength(100);
            e.Property(x => x.Remarks).HasMaxLength(500);
            e.Property(x => x.RawReport).HasMaxLength(300).IsRequired();
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

            e.Property(x => x.Cover).HasMaxLength(3).IsRequired();
            e.Property(x => x.CloudType).HasMaxLength(3);

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

            e.Property(x => x.PhenomenonKind).HasMaxLength(7).IsRequired();
            e.Property(x => x.Intensity).HasMaxLength(2).IsRequired();
            e.Property(x => x.Descriptor).HasMaxLength(2);
            e.Property(x => x.Precipitation).HasMaxLength(20);
            e.Property(x => x.Obscuration).HasMaxLength(2);
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
            e.Property(x => x.Trend).HasMaxLength(1);

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

            e.Property(x => x.ReportType).HasMaxLength(7).IsRequired();
            e.Property(x => x.StationIcao).HasMaxLength(4).IsFixedLength().IsRequired();
            e.Property(x => x.RawReport).HasColumnType("nvarchar(max)").IsRequired();
            e.Property(x => x.IssuanceUtc).IsRequired();
            e.Property(x => x.ValidFromUtc).IsRequired();
            e.Property(x => x.ValidToUtc).IsRequired();
            e.Property(x => x.ReceivedUtc).IsRequired();

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

            e.Property(x => x.ChangeType).HasMaxLength(12).IsRequired();
            e.Property(x => x.WindUnit).HasMaxLength(3);
            e.Property(x => x.RawSkyConditions).HasMaxLength(150);
            e.Property(x => x.RawWeather).HasMaxLength(100);

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

            e.Property(x => x.Cover).HasMaxLength(3).IsRequired();
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

            e.Property(x => x.Intensity).HasMaxLength(2).IsRequired();
            e.Property(x => x.Descriptor).HasMaxLength(2);
            e.Property(x => x.Precipitation).HasMaxLength(20);
            e.Property(x => x.Obscuration).HasMaxLength(2);
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

            e.Property(x => x.RecipientId).HasMaxLength(100).IsRequired();
            e.Property(x => x.Email).HasMaxLength(200).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Timezone).HasMaxLength(100).IsRequired();
            e.Property(x => x.ScheduledSendHours).HasMaxLength(50);
            e.Property(x => x.Address).HasMaxLength(500);
            e.Property(x => x.LocalityName).HasMaxLength(200);
            e.Property(x => x.MetarIcao).HasMaxLength(100);
            e.Property(x => x.TafIcao).HasMaxLength(10);
            e.Property(x => x.TempUnit).HasMaxLength(10).IsRequired();
            e.Property(x => x.PressureUnit).HasMaxLength(10).IsRequired();
            e.Property(x => x.WindSpeedUnit).HasMaxLength(10).IsRequired();
            e.Property(x => x.PrecipUnit).HasMaxLength(10).IsRequired();
            e.Property(x => x.NumberFormat).HasMaxLength(40);

            e.HasIndex(x => x.RecipientId)
             .IsUnique()
             .HasDatabaseName("UX_Recipients_RecipientId");

            // Membership FK to Locality (WX-125). No inverse collection on Locality —
            // membership is the single fact stored in Recipients.LocalityId; members are
            // retrieved by querying (matches the CommittedSend → ForecastSnapshot pattern).
            // RESTRICT: a locality cannot be deleted while recipients still reference it —
            // they must be reassigned first, so deletion never silently drops members.
            e.HasOne(x => x.Locality)
             .WithMany()
             .HasForeignKey(x => x.LocalityId)
             .OnDelete(DeleteBehavior.Restrict);

            // Language FK (WX-166). RESTRICT mirrors the disable-while-assigned rule:
            // a language cannot be removed while recipients still reference it. Null
            // LanguageId means "use the service default language".
            e.HasOne(x => x.Language)
             .WithMany()
             .HasForeignKey(x => x.LanguageId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // ── Languages ────────────────────────────────────────────────────────

        modelBuilder.Entity<Language>(e =>
        {
            e.ToTable("Languages");
            e.HasKey(x => x.Id);

            e.Property(x => x.IsoCode).HasMaxLength(2).IsRequired();   // ISO 639-1 is exactly two letters
            e.Property(x => x.DisplayName).HasMaxLength(100).IsRequired();
            e.Property(x => x.IsEnabled).IsRequired();

            e.HasIndex(x => x.IsoCode)
             .IsUnique()
             .HasDatabaseName("UX_Languages_IsoCode");

            e.Property(x => x.CultureName).HasMaxLength(20);       // IETF tag, e.g. "es-US"
            e.Property(x => x.GenerationError).HasMaxLength(1000); // BLOCKED reason (WX-172)
        });

        // ── LanguageTemplates (WX-167) ───────────────────────────────────────

        modelBuilder.Entity<LanguageTemplate>(e =>
        {
            e.ToTable("LanguageTemplates");
            e.HasKey(x => x.Id);

            e.Property(x => x.Token).HasMaxLength(60).IsRequired();
            e.Property(x => x.Phrase).HasMaxLength(500).IsRequired();
            e.Property(x => x.ContextInfo).HasMaxLength(1000).IsRequired();
            e.Property(x => x.ContextKind)
             .HasConversion<string>()      // store the enum name ("Example"/"Hint"), not its ordinal
             .HasMaxLength(10)
             .IsRequired();
            e.Property(x => x.Note).HasMaxLength(1000);
            e.Property(x => x.Representable).IsRequired();
            e.Property(x => x.ReviewedBy).HasMaxLength(100);

            // One phrase per (language, token). RESTRICT on delete mirrors Languages:
            // a language row carrying templates can't be deleted out from under them.
            e.HasOne(x => x.Language)
             .WithMany(l => l.Templates)
             .HasForeignKey(x => x.LanguageId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.LanguageId, x.Token })
             .IsUnique()
             .HasDatabaseName("UX_LanguageTemplates_LanguageId_Token");
        });

        // ── PromptGlossaryTokens (WX-238) ────────────────────────────────────

        modelBuilder.Entity<PromptGlossaryToken>(e =>
        {
            e.ToTable("PromptGlossaryTokens");
            e.HasKey(x => x.Id);

            e.Property(x => x.Token).HasMaxLength(60).IsRequired();
            e.Property(x => x.Note).HasMaxLength(1000);

            // One row per concept (language-neutral). The set the reconciler's glossary is built from.
            e.HasIndex(x => x.Token)
             .IsUnique()
             .HasDatabaseName("UX_PromptGlossaryTokens_Token");
        });

        // ── Localities ───────────────────────────────────────────────────────

        modelBuilder.Entity<Locality>(e =>
        {
            e.ToTable("Localities");
            e.HasKey(x => x.Id);

            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.MetarIcao).HasMaxLength(100);
            e.Property(x => x.TafIcao).HasMaxLength(10);
            e.Property(x => x.Timezone).HasMaxLength(100).IsRequired();
            e.Property(x => x.ScheduledSendHours).HasMaxLength(50);
            e.Property(x => x.CentroidLat);
            e.Property(x => x.CentroidLon);

            e.HasIndex(x => x.Name)
             .IsUnique()
             .HasDatabaseName("UX_Localities_Name");
        });

        // ── GlobalSettings ────────────────────────────────────────────────────────

        modelBuilder.Entity<GlobalSettings>(e =>
        {
            e.ToTable("GlobalSettings");
            e.HasKey(x => x.Id);

            e.Property(x => x.ClaudeApiKey).HasMaxLength(500);
            e.Property(x => x.GeminiApiKey).HasMaxLength(500);
            e.Property(x => x.SmtpUsername).HasMaxLength(200);
            e.Property(x => x.SmtpPassword).HasMaxLength(200);
            e.Property(x => x.SmtpFromAddress).HasMaxLength(200);
        });

        // ── Config (WX-313) ──────────────────────────────────────────────────
        // Key/value application configuration, layered into IConfiguration by a
        // DB-backed provider.  Key is the natural PK — the Section:SubKey path.
        modelBuilder.Entity<Config>(e =>
        {
            e.ToTable("Config");
            e.HasKey(x => x.Key);

            e.Property(x => x.Key).HasMaxLength(200);
            e.Property(x => x.Value);            // nvarchar(max) — no length cap
            e.Property(x => x.UpdatedUtc).IsRequired();
        });

        // ── QaRerunRequests (WX-235) ─────────────────────────────────────────
        // One row per language, transitioned in place (not appended), so IsoCode is unique:
        // WxManager writes Running on the button press; the WxReport.Svc worker claims it
        // (Status = Running AND StartedAtUtc IS NULL → set StartedAtUtc) and writes the terminal state.
        modelBuilder.Entity<QaRerunRequest>(e =>
        {
            e.ToTable("QaRerunRequests");
            e.HasKey(x => x.Id);

            e.Property(x => x.IsoCode).HasMaxLength(2).IsRequired();   // ISO 639-1 is exactly two letters
            e.Property(x => x.Status)
             .HasConversion<string>()      // store the enum name ("Running"/"Succeeded"/"Failed"), not its ordinal
             .HasMaxLength(12)
             .IsRequired();
            e.Property(x => x.RequestedAtUtc).IsRequired();
            e.Property(x => x.ResultStamp).HasMaxLength(40);
            e.Property(x => x.Error).HasMaxLength(1000);
            e.Property(x => x.RequestedBy).HasMaxLength(100);

            e.HasIndex(x => x.IsoCode)
             .IsUnique()
             .HasDatabaseName("UX_QaRerunRequests_IsoCode");
        });

        // ── RecipientStates ──────────────────────────────────────────────────

        modelBuilder.Entity<RecipientState>(e =>
        {
            e.ToTable("RecipientStates");
            e.HasKey(x => x.Id);

            e.Property(x => x.RecipientId).HasMaxLength(100).IsRequired();
            e.Property(x => x.LastClaudeInputHash).HasMaxLength(200);
            e.Property(x => x.LastSentInputHash).HasMaxLength(200);
            e.Property(x => x.LastMetarIcao).HasMaxLength(4).IsFixedLength();

            e.HasIndex(x => x.RecipientId)
             .IsUnique()
             .HasDatabaseName("UX_RecipientStates_RecipientId");
        });

        // ── LocalityStates ───────────────────────────────────────────────────

        modelBuilder.Entity<LocalityState>(e =>
        {
            e.ToTable("LocalityStates");
            e.HasKey(x => x.Id);

            e.Property(x => x.LastClaudeInputHash).HasMaxLength(200);
            e.Property(x => x.LastSentInputHash).HasMaxLength(200);
            e.Property(x => x.LastDegradedInputHash).HasMaxLength(200);
            e.Property(x => x.LastMetarIcao).HasMaxLength(4).IsFixedLength();

            e.HasIndex(x => x.LocalityId)
             .IsUnique()
             .HasDatabaseName("UX_LocalityStates_LocalityId");

            // The state is owned by its locality — delete it with the locality
            // (unlike the Recipient→Locality FK, which RESTRICTs to protect members).
            e.HasOne(x => x.Locality)
             .WithMany()
             .HasForeignKey(x => x.LocalityId)
             .OnDelete(DeleteBehavior.Cascade);
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

            e.Property(x => x.IcaoId).HasMaxLength(4).IsFixedLength().IsRequired();
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Municipality).HasMaxLength(100);
            e.Property(x => x.Lat);
            e.Property(x => x.Lon);
            e.Property(x => x.ElevationFt);
            e.Property(x => x.Region).HasMaxLength(100);
            e.Property(x => x.RegionCode).HasMaxLength(10);
            e.Property(x => x.RegionAbbr).HasMaxLength(10);
            e.Property(x => x.Country).HasMaxLength(100);
            e.Property(x => x.CountryCode).HasMaxLength(2).IsFixedLength();
            e.Property(x => x.CountryAbbr).HasMaxLength(10);
        });

        // ── GfsGrid ──────────────────────────────────────────────────────────

        modelBuilder.Entity<GfsGridPoint>(e =>
        {
            e.ToTable("GfsGrid");
            e.HasKey(x => x.Id);

            e.Property(x => x.ModelRunUtc).IsRequired();
            e.Property(x => x.ForecastHour).IsRequired();
            e.Property(x => x.Lat).IsRequired();
            e.Property(x => x.Lon).IsRequired();

            // Prevent duplicate ingestion of the same grid point / run / hour.
            e.HasIndex(x => new { x.ModelRunUtc, x.ForecastHour, x.Lat, x.Lon })
             .IsUnique()
             .HasDatabaseName("UX_GfsGrid_Run_Hour_LatLon");

            // Support fast "all points for run X at forecast hour Y" queries.
            e.HasIndex(x => new { x.ModelRunUtc, x.ForecastHour })
             .HasDatabaseName("IX_GfsGrid_Run_Hour");
        });

        // ── ForecastSnapshots ────────────────────────────────────────────────

        modelBuilder.Entity<ForecastSnapshot>(e =>
        {
            e.ToTable("ForecastSnapshots");
            e.HasKey(x => x.Id);

            e.Property(x => x.StationIcao).HasMaxLength(4).IsFixedLength().IsRequired();
            e.Property(x => x.GeneratedAtUtc).IsRequired();
            e.Property(x => x.SchemaVersion)
             .IsRequired()
             .HasDefaultValue(ForecastSnapshotBody.SchemaVersionCurrent);
            e.Property(x => x.Body).HasColumnType("nvarchar(max)").IsRequired();

            // One snapshot per station per commit instant; also supports
            // the "most recent snapshot for station X" lookup.
            e.HasIndex(x => new { x.StationIcao, x.GeneratedAtUtc })
             .IsUnique()
             .HasDatabaseName("UX_ForecastSnapshots_Station_GeneratedAt");
        });

        // ── CommittedSends ───────────────────────────────────────────────────

        modelBuilder.Entity<CommittedSend>(e =>
        {
            e.ToTable("CommittedSends");
            e.HasKey(x => x.Id);

            e.Property(x => x.RecipientId).HasMaxLength(100).IsRequired();
            e.Property(x => x.ReasoningTrace).HasColumnType("nvarchar(max)");
            e.Property(x => x.EmailBody).HasColumnType("nvarchar(max)");
            e.Property(x => x.CreatedAtUtc).IsRequired();
            e.Property(x => x.SchemaVersion)
             .IsRequired()
             .HasDefaultValue(CommittedSend.SchemaVersionCurrent);

            e.HasOne(x => x.ForecastSnapshot)
             .WithMany()
             .HasForeignKey(x => x.ForecastSnapshotId)
             .OnDelete(DeleteBehavior.Restrict);

            // Supports the "most recent send to recipient X" query that WX-79
            // and WX-80 lean on; DESC on CreatedAtUtc favors latest-first scans.
            e.HasIndex(x => new { x.RecipientId, x.CreatedAtUtc })
             .IsDescending(false, true)
             .HasDatabaseName("IX_CommittedSends_Recipient_CreatedAt");
        });
    }
}