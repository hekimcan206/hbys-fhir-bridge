using HbysFhirBridge.Sync;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;

namespace HbysFhirBridge.Worker;

internal static class Observability
{
    /// <summary>
    /// Structured JSON logs on stdout plus OpenTelemetry traces (one span per batch, child spans per FHIR request)
    /// and metrics. The OTLP exporter is enabled only when OTEL_EXPORTER_OTLP_ENDPOINT is set.
    /// </summary>
    public static WebApplicationBuilder AddObservability(this WebApplicationBuilder builder)
    {
        builder.Services.AddSerilog((services, logger) => logger
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.Console(new RenderedCompactJsonFormatter()));

        var serviceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? "hbys-fhir-bridge";
        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName,
                serviceVersion: typeof(Observability).Assembly.GetName().Version?.ToString(3)))
            .WithTracing(tracing => tracing
                .AddSource(BridgeTelemetry.Name)
                .AddHttpClientInstrumentation()
                .AddAspNetCoreInstrumentation(o => o.Filter = c => !c.Request.Path.StartsWithSegments("/health")))
            .WithMetrics(metrics => metrics
                .AddMeter(BridgeTelemetry.Name)
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation());

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        return builder;
    }
}
