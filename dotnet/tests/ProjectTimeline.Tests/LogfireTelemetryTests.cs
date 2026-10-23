using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.UI.WebApi;
using Xunit;

namespace ProjectTimeline.Tests;

[Collection(AgentTelemetryCollection.Name)]
public sealed class LogfireTelemetryTests
{
    private const string DummyToken = "pylf_v1_dummy_offline_only";
    private const string EuEndpoint = "https://logfire-eu.pydantic.dev/v1/traces";
    private const string UsEndpoint = "https://logfire-us.pydantic.dev/v1/traces";

    private static IConfiguration Configuration(string? endpoint = null, string? token = null, string? service = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logfire:Endpoint"] = endpoint,
            ["Logfire:Token"] = token,
            ["Logfire:ServiceName"] = service
        }).Build();

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", "", null)]
    [InlineData(" ", "\t", null)]
    [InlineData(null, null, "service-only")]
    public void Absent_configuration_registers_nothing(string? endpoint, string? token, string? service)
    {
        var services = new ServiceCollection();
        Assert.Same(services, services.AddLogfireTelemetry(Configuration(endpoint, token, service)));
        Assert.Empty(services);
        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<TracerProvider>());
    }

    [Theory]
    [InlineData("endpoint-only")]
    [InlineData("token-only")]
    [InlineData("blank-token")]
    public void Partial_configuration_fails_without_echoing_values(string scenario)
    {
        var endpoint = scenario == "token-only" ? null : EuEndpoint;
        var token = scenario == "token-only" ? DummyToken : scenario == "blank-token" ? " " : null;
        var services = new ServiceCollection();
        var error = Assert.Throws<InvalidOperationException>(() => services.AddLogfireTelemetry(Configuration(endpoint, token)));
        Assert.Equal("Configure both Logfire:Endpoint and Logfire:Token to enable agent tracing.", error.Message);
        Assert.Null(error.InnerException);
        Assert.Empty(services);
    }

    [Theory]
    [InlineData("not-a-uri")]
    [InlineData("http://logfire-eu.pydantic.dev/v1/traces")]
    [InlineData("https://logfire.pydantic.dev/v1/traces")]
    [InlineData("https://logfire-eu.pydantic.dev")]
    [InlineData("https://logfire-us.pydantic.dev/v1/traces/")]
    [InlineData("https://logfire-eu.pydantic.dev/v1/traces?credential=dummy")]
    [InlineData("https://logfire-eu.pydantic.dev/v1/traces#dummy")]
    [InlineData("https://dummy:dummy@logfire-eu.pydantic.dev/v1/traces")]
    [InlineData("https://logfire-us.pydantic.dev:8443/v1/traces")]
    [InlineData("https://example.invalid/v1/traces")]
    public void Invalid_endpoint_fails_safely_before_registration(string endpoint)
    {
        var services = new ServiceCollection();
        var error = Assert.Throws<InvalidOperationException>(() => services.AddLogfireTelemetry(Configuration(endpoint, DummyToken)));
        Assert.Equal("Logfire:Endpoint must be an EU or US Logfire HTTPS endpoint ending in /v1/traces, without credentials, query or fragment.", error.Message);
        Assert.Null(error.InnerException);
        Assert.Empty(services);
    }

    [Theory]
    [InlineData("scheme-prefix")]
    [InlineData("newline")]
    [InlineData("header-list")]
    [InlineData("equals")]
    public void Invalid_token_fails_safely_before_registration(string scenario)
    {
        var token = scenario switch
        {
            "scheme-prefix" => "Bearer dummy",
            "newline" => "dummy\r\nInjected: dummy",
            "header-list" => "dummy,Other=dummy",
            _ => "dummy=other"
        };
        var services = new ServiceCollection();
        var error = Assert.Throws<InvalidOperationException>(() => services.AddLogfireTelemetry(Configuration(EuEndpoint, token)));
        Assert.Equal("Logfire:Token must be a write token containing only ASCII letters, digits, underscores, hyphens or dots; do not add Bearer.", error.Message);
        Assert.Null(error.InnerException);
        Assert.Empty(services);
    }

    [Theory]
    [InlineData(EuEndpoint, null, "ProjectTimeline.WebApi")]
    [InlineData(EuEndpoint, "", "ProjectTimeline.WebApi")]
    [InlineData(UsEndpoint, " ", "ProjectTimeline.WebApi")]
    [InlineData(UsEndpoint, "timeline-offline-test", "timeline-offline-test")]
    public async Task Host_exports_framework_spans_as_protobuf_with_resource_and_raw_auth(
        string endpoint, string? service, string expectedService)
    {
        using var handler = new CaptureHandler();
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddLogfireTelemetry(Configuration(endpoint, DummyToken, service));
            // Override the SDK transport through its public options, not a production-only seam.
            services.PostConfigureAll<OtlpExporterOptions>(options => options.HttpClientFactory = () => new HttpClient(handler, disposeHandler: false));
        }).Build();
        using var framework = new ActivitySource(AgentTelemetry.SourceName);
        using var unrelated = new ActivitySource("Microsoft.Agents.AI");
        using var http = new ActivitySource("System.Net.Http");
        await host.StartAsync();
        try
        {
            Assert.True(framework.HasListeners());
            Assert.False(unrelated.HasListeners());
            Assert.False(http.HasListeners());
            var provider = host.Services.GetRequiredService<TracerProvider>();
            Assert.Null(host.Services.GetService<OpenTelemetry.Metrics.MeterProvider>());
            Assert.Empty(host.Services.GetServices<Microsoft.Extensions.Logging.ILoggerProvider>()
                .OfType<OpenTelemetry.Logs.OpenTelemetryLoggerProvider>());
            var sessionId = Guid.NewGuid();
            using (var session = AgentCostSession.Start(new Dictionary<string, AgentTokenPricing>
                { [SummaryChat.Sentinel] = new(2m, 5m) }, sessionId))
            {
                using var chat = new UsageTrackingChatClient(new SummaryChat());
                await chat.GetResponseAsync([new(ChatRole.User, SummaryChat.Sentinel)]);
                using (var span = framework.StartActivity("agent.run"))
                {
                    Assert.NotNull(span);
                    span.SetTag("agent.run.outcome", "success");
                }
                using (var span = framework.StartActivity("agent.harness.run")) Assert.NotNull(span);
            }
            Assert.True(provider.ForceFlush(10_000));
            var requests = handler.Requests.ToArray();
            Assert.NotEmpty(requests);
            Assert.All(requests, request =>
            {
                Assert.Equal(new Uri(endpoint), request.Endpoint);
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("application/x-protobuf", request.ContentType);
                // Never include header values in assertion diagnostics.
                Assert.True(request.RawAuthorizationIsCorrect, "Authorization must carry the raw write token, without Bearer.");
                Assert.NotEmpty(request.Body);
                var payload = Encoding.UTF8.GetString(request.Body);
                Assert.False(payload.Contains(DummyToken, StringComparison.Ordinal), "Credentials must not appear in the payload.");
                Assert.False(payload.Contains(endpoint, StringComparison.Ordinal), "Exporter endpoint must not appear in the payload.");
                Assert.DoesNotContain(SummaryChat.Sentinel, payload);
            });
            var resourceSpans = requests.SelectMany(request => Messages(request.Body, 1)).ToArray();
            Assert.NotEmpty(resourceSpans);
            Assert.All(resourceSpans, resourceSpan =>
            {
                var resource = Assert.Single(Messages(resourceSpan, 1));
                var attributes = Messages(resource, 1).ToDictionary(
                    attribute => Text(attribute, 1), attribute => Text(Assert.Single(Messages(attribute, 2)), 1));
                Assert.Equal(expectedService, attributes["service.name"]);
            });
            var scopeSpans = resourceSpans.SelectMany(resourceSpan => Messages(resourceSpan, 2)).ToArray();
            Assert.All(scopeSpans, scopeSpan =>
                Assert.Equal(AgentTelemetry.SourceName, Text(Assert.Single(Messages(scopeSpan, 1)), 1)));
            var spans = scopeSpans.SelectMany(scopeSpan => Messages(scopeSpan, 2)).ToArray();
            Assert.Equal(new[] { "agent.harness.run", "agent.run", "agent.session" }, spans.Select(span => Text(span, 5)).OrderBy(name => name).ToArray());
            var summary = Assert.Single(spans, span => Text(span, 5) == "agent.session");
            var summaryId = Assert.Single(Messages(summary, 2));
            Assert.All(spans.Where(span => Text(span, 5) != "agent.session"), span =>
                Assert.Equal(summaryId, Assert.Single(Messages(span, 4))));
            var tags = Messages(summary, 9).ToDictionary(attribute => Text(attribute, 1),
                attribute => AnyValue(Assert.Single(Messages(attribute, 2))));
            Assert.Equal(sessionId.ToString("D"), tags["agent.session.id"]);
            Assert.Equal("USD", tags["agent.session.currency"]);
            Assert.Equal(1L, tags["agent.session.call_count"]);
            Assert.Equal(120L, tags["agent.session.total_tokens"]);
            Assert.Equal(0.0003d, tags["agent.session.estimated_total_cost"]);
            Assert.Equal(0.0003d, tags["agent.session.known_estimated_cost"]);
            Assert.Equal(true, tags["agent.session.usage_complete"]);
            Assert.Equal(true, tags["agent.session.estimated_cost_complete"]);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
        Assert.False(framework.HasListeners());
    }

    private sealed class SummaryChat : IChatClient
    {
        public const string Sentinel = "PRIVATE_OTLP_SESSION_PAYLOAD";
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Sentinel))
            {
                ModelId = Sentinel, Usage = new() { InputTokenCount = 100, OutputTokenCount = 20, TotalTokenCount = 120 }
            });
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    // Assert the actual OTLP AnyValue type, not just a text match in the protobuf payload.
    internal static object AnyValue(byte[] data)
    {
        var offset = 0;
        return Varint(data, ref offset) switch
        {
            10 => Text(data, 1),
            16 => Varint(data, ref offset) != 0,
            24 => checked((long)Varint(data, ref offset)),
            33 => BitConverter.ToDouble(data, offset),
            _ => throw new InvalidDataException("Unexpected OTLP attribute type.")
        };
    }

    internal sealed record Request(Uri? Endpoint, HttpMethod Method, string? ContentType, bool RawAuthorizationIsCorrect, byte[] Body);

    internal sealed class CaptureHandler : HttpMessageHandler
    {
        public System.Collections.Concurrent.ConcurrentQueue<Request> Requests { get; } = new();

        // The OTLP exporter can use HttpClient's synchronous Send path.
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            SendAsync(request, cancellationToken).GetAwaiter().GetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var rawAuthIsCorrect = request.Headers.TryGetValues("Authorization", out var headers)
                && headers.SequenceEqual(new[] { DummyToken });
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            Requests.Enqueue(new(request.RequestUri, request.Method, request.Content.Headers.ContentType?.MediaType, rawAuthIsCorrect, body));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        }
    }

    // Minimal protobuf reader for the OTLP length-delimited fields asserted above.
    // It also skips the numeric/fixed-width fields emitted by the real exporter.
    internal static List<byte[]> Messages(byte[] data, int field)
    {
        var messages = new List<byte[]>();
        var offset = 0;
        while (offset < data.Length)
        {
            var key = Varint(data, ref offset);
            switch (key & 7)
            {
                case 0: Varint(data, ref offset); break;
                case 1: offset += 8; break;
                case 2:
                    var length = checked((int)Varint(data, ref offset));
                    if (key >> 3 == (ulong)field) messages.Add(data.AsSpan(offset, length).ToArray());
                    offset += length;
                    break;
                case 5: offset += 4; break;
                default: throw new InvalidDataException("Unexpected protobuf wire type.");
            }
        }
        Assert.Equal(data.Length, offset);
        return messages;
    }

    internal static string Text(byte[] data, int field) => Encoding.UTF8.GetString(Assert.Single(Messages(data, field)));

    private static ulong Varint(byte[] data, ref int offset)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64 && offset < data.Length; shift += 7)
        {
            var current = data[offset++];
            value |= (ulong)(current & 127) << shift;
            if ((current & 128) == 0) return value;
        }
        throw new InvalidDataException("Invalid protobuf varint.");
    }
}
