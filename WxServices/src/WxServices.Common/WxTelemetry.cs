using log4net;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;

namespace WxServices.Common;

/// <summary>
/// Shared OpenTelemetry metrics wiring for the WxServices hosts (WX-159). Consolidates the telemetry
/// block that was copy-pasted across all four services: the <c>Telemetry:Enabled</c> gate, the OTLP
/// HTTP exporter to <c>Telemetry:OtlpEndpoint</c> (default <c>http://localhost:4318/v1/metrics</c>) on a
/// 10 s interval, and — the piece that was missing before WX-159 — the <see cref="OtelExportDiagnostics"/>
/// listener, so an unreachable collector reaches the log instead of only the dashboards. Each service
/// supplies its own meter(s) and histogram view(s) through <paramref name="configureMetrics"/>.
/// </summary>
public static class WxTelemetry
{
    private static readonly ILog Logger = LogManager.GetLogger(typeof(WxTelemetry));
    private const string DefaultOtlpEndpoint = "http://localhost:4318/v1/metrics";
    private const int ExportIntervalMs = 10_000;

    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">Source of <c>Telemetry:Enabled</c> and <c>Telemetry:OtlpEndpoint</c>.</param>
    /// <param name="configureMetrics">Adds the service's own meter(s) and view(s) to the metrics builder.</param>
    public static IServiceCollection AddWxTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<MeterProviderBuilder> configureMetrics)
    {
        ArgumentNullException.ThrowIfNull(configureMetrics);

        var enabled = bool.TryParse(configuration["Telemetry:Enabled"], out var e) && e;
        var endpoint = configuration["Telemetry:OtlpEndpoint"] ?? DefaultOtlpEndpoint;

        services.AddOpenTelemetry().WithMetrics(m =>
        {
            configureMetrics(m);

            if (!enabled)
            {
                Logger.Info("Telemetry disabled. Set Telemetry:Enabled=true in appsettings to export metrics.");
                return;
            }

            // Fail loudly and specifically on a misconfigured endpoint rather than with a bare UriFormatException.
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
                throw new InvalidOperationException(
                    $"Telemetry:OtlpEndpoint is not a valid absolute URI: '{endpoint}'. Expected e.g. {DefaultOtlpEndpoint}.");

            m.AddOtlpExporter((o, r) =>
            {
                o.Endpoint = endpointUri;
                o.Protocol = OtlpExportProtocol.HttpProtobuf;
                r.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = ExportIntervalMs;
            });
            OtelExportDiagnostics.Enable();
            Logger.Info($"Telemetry enabled. Exporting metrics to {endpoint}.");
        });

        return services;
    }
}