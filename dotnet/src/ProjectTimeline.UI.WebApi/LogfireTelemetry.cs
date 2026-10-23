using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ProjectTimeline.BusinessLogic.Agents.Framework;

namespace ProjectTimeline.UI.WebApi;

/// <summary>Opt-in, host-owned export of minimal HTTP roots and safe framework run spans.</summary>
public static class LogfireTelemetry
{
    public static IServiceCollection AddLogfireTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var endpointSetting = configuration["Logfire:Endpoint"];
        var token = configuration["Logfire:Token"];
        var hasEndpoint = !string.IsNullOrWhiteSpace(endpointSetting);
        var hasToken = !string.IsNullOrWhiteSpace(token);
        if (!hasEndpoint && !hasToken) return services;
        if (!hasEndpoint || !hasToken)
            throw new InvalidOperationException("Configure both Logfire:Endpoint and Logfire:Token to enable agent tracing.");

        // Validate before registering anything, and never include supplied values in errors.
        if (!Uri.TryCreate(endpointSetting, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps
            || endpoint.Host is not ("logfire-eu.pydantic.dev" or "logfire-us.pydantic.dev")
            || !endpoint.IsDefaultPort || endpoint.AbsolutePath != "/v1/traces"
            || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new InvalidOperationException("Logfire:Endpoint must be an EU or US Logfire HTTPS endpoint ending in /v1/traces, without credentials, query or fragment.");

        // OTLP headers use comma-separated key=value syntax. Accept a raw write token only.
        if (token!.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.')))
            throw new InvalidOperationException("Logfire:Token must be a write token containing only ASCII letters, digits, underscores, hyphens or dots; do not add Bearer.");

        var serviceName = configuration["Logfire:ServiceName"];
        if (string.IsNullOrWhiteSpace(serviceName)) serviceName = "ProjectTimeline.WebApi";
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                .AddSource(AgentTelemetry.SourceName, AgentTelemetry.NativeSourceName)
                .AddAspNetCoreInstrumentation(options =>
                {
                    options.RecordException = false;
                    options.EnableAspNetCoreSignalRSupport = false;
                    options.EnableRazorComponentsSupport = false;
                    options.EnrichWithHttpRequest = LogfireHttpPrivacyProcessor.CaptureMethod;
                })
                // Keep default ParentBased sampling; sanitize before batch/exporter enqueue.
                .AddProcessor(new LogfireHttpPrivacyProcessor())
                .AddProcessor(new LogfireAgentPrivacyProcessor())
                .AddOtlpExporter(options =>
                {
                    // Programmatic HTTP/protobuf endpoints are used as-is, including /v1/traces.
                    options.Endpoint = endpoint;
                    options.Protocol = OtlpExportProtocol.HttpProtobuf;
                    options.Headers = $"Authorization={token}";
                }));
        return services;
    }
}
