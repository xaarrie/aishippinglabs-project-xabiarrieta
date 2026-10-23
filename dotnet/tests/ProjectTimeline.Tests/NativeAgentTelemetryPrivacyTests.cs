using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.UI.WebApi;
using Xunit;
using static ProjectTimeline.Tests.LogfireTelemetryTests;

namespace ProjectTimeline.Tests;

[Collection(AgentTelemetryCollection.Name)]
public sealed class NativeAgentTelemetryPrivacyTests
{
    // A literal lets RED exercise missing registration before the public constant exists.
    private const string NativeSource = "ProjectTimeline.Agents.Native";
    private const string Secret = "PRIVATE_NATIVE_PAYLOAD_SENTINEL";

    [Fact]
    public async Task Host_subscribes_to_exact_native_source_only_when_configured()
    {
        using var fixture = await Fixture.Start();
        using var native = new ActivitySource(NativeSource);
        using var unrelated = new ActivitySource("Microsoft.Agents.AI");
        using var nearMatch = new ActivitySource(NativeSource + "." + Secret);
        Assert.Equal(NativeSource, AgentTelemetry.NativeSourceName);
        Assert.True(native.HasListeners());
        Assert.False(unrelated.HasListeners());
        Assert.False(nearMatch.HasListeners());
        Assert.Null(fixture.Host.Services.GetService<OpenTelemetry.Metrics.MeterProvider>());
        Assert.Empty(fixture.Host.Services.GetServices<Microsoft.Extensions.Logging.ILoggerProvider>()
            .OfType<OpenTelemetry.Logs.OpenTelemetryLoggerProvider>());
    }

    [Theory]
    [InlineData("invoke_agent")]
    [InlineData("chat")]
    [InlineData("execute_tool")]
    [InlineData(Secret)]
    public async Task Final_wire_has_fixed_names_safe_labels_numeric_usage_and_original_context(string operation)
    {
        using var fixture = await Fixture.Start();
        using var wrapper = new ActivitySource(AgentTelemetry.SourceName);
        using var native = new ActivitySource(NativeSource);
        ActivityTraceId trace;
        ActivitySpanId parentId;
        ActivitySpanId nativeId;
        using (var session = AgentCostSession.Start(sessionId: Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")))
        using (var parent = wrapper.StartActivity("agent.run"))
        {
            Assert.NotNull(parent);
            parent.SetTag("agent.run.outcome", "success");
            trace = parent.TraceId;
            parentId = parent.SpanId;
            using var span = native.StartActivity(Secret, ActivityKind.Client,
                parentContext: parent.Context, tags: new ActivityTagsCollection
                {
                    ["gen_ai.operation.name"] = operation,
                    ["gen_ai.agent.name"] = "timelineFactExtraction",
                    ["gen_ai.request.model"] = "gpt-4o-mini",
                    ["gen_ai.response.model"] = Secret,
                    ["gen_ai.provider.name"] = Secret,
                    ["gen_ai.tool.name"] = Secret,
                    ["gen_ai.usage.input_tokens"] = 123L,
                    ["gen_ai.usage.output_tokens"] = 7,
                    [Secret] = Secret
                });
            Assert.NotNull(span);
            nativeId = span.SpanId;
            span.DisplayName = Secret;
            // Mutations after OnStart must still be removed before exporter enqueue.
            span.SetTag("gen_ai.input.messages", Secret);
            span.SetTag("gen_ai.output.messages", Secret);
            span.SetTag("gen_ai.system_instructions", Secret);
            span.SetTag("gen_ai.tool.call.arguments", Secret);
            span.SetTag("gen_ai.tool.call.result", Secret);
            span.SetTag("error.type", Secret);
            span.SetStatus(ActivityStatusCode.Error, Secret);
            span.TraceStateString = "vendor=" + Secret;
        }
        var spans = fixture.Flush();
        Assert.Equal(3, spans.Length);
        var child = Assert.Single(spans, span => Text(span, 5) == (operation == Secret ? "other" : operation));
        Assert.Equal(Convert.FromHexString(trace.ToHexString()), Assert.Single(Messages(child, 1)));
        Assert.Equal(Convert.FromHexString(nativeId.ToHexString()), Assert.Single(Messages(child, 2)));
        Assert.Equal(Convert.FromHexString(parentId.ToHexString()), Assert.Single(Messages(child, 4)));
        Assert.Empty(Messages(child, 3));
        Assert.Empty(Messages(child, 11));
        Assert.Empty(Messages(child, 13));
        var status = Assert.Single(Messages(child, 15));
        Assert.Empty(Messages(status, 2));
        Assert.Equal(new byte[] { 24, 2 }, status); // Error enum retained, description removed.
        Assert.Equal(new Dictionary<string, object>
        {
            ["gen_ai.operation.name"] = operation == Secret ? "other" : operation,
            ["gen_ai.agent.name"] = "timelineFactExtraction",
            ["gen_ai.request.model"] = "gpt-4o-mini",
            ["gen_ai.response.model"] = "other",
            ["gen_ai.provider.name"] = "other",
            ["gen_ai.usage.input_tokens"] = 123L,
            ["gen_ai.usage.output_tokens"] = 7L
        }.OrderBy(pair => pair.Key), Tags(child).OrderBy(pair => pair.Key));
        var run = Assert.Single(spans, span => Text(span, 5) == "agent.run");
        Assert.Equal("success", Tags(run)["agent.run.outcome"]);
        var summary = Assert.Single(spans, span => Text(span, 5) == "agent.session");
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", Tags(summary)["agent.session.id"]);
        Assert.Equal(0L, Tags(summary)["agent.session.call_count"]);
        Assert.Equal(Assert.Single(Messages(summary, 2)), Assert.Single(Messages(run, 4)));
        fixture.AssertWireSafe();
    }

    [Theory]
    [InlineData("exception")]
    [InlineData("event-name")]
    [InlineData("event-attribute")]
    [InlineData("empty-event")]
    [InlineData("link-context")]
    [InlineData("link-attribute")]
    [InlineData("empty-link")]
    public async Task Any_native_event_or_link_fails_closed_without_dropping_safe_parent(string contamination)
    {
        using var fixture = await Fixture.Start();
        using var wrapper = new ActivitySource(AgentTelemetry.SourceName);
        using var native = new ActivitySource(NativeSource);
        ActivitySpanId droppedId;
        using (var parent = wrapper.StartActivity("agent.run"))
        {
            Assert.NotNull(parent);
            using var span = native.StartActivity(Secret);
            Assert.NotNull(span);
            droppedId = span.SpanId;
            span.SetTag("gen_ai.operation.name", "invoke_agent");
            span.SetStatus(ActivityStatusCode.Error, Secret);
            switch (contamination)
            {
                case "exception":
                    span.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
                    {
                        ["exception.type"] = Secret, ["exception.message"] = Secret, ["exception.stacktrace"] = Secret
                    }));
                    break;
                case "event-name": span.AddEvent(new ActivityEvent(Secret)); break;
                case "event-attribute":
                    span.AddEvent(new ActivityEvent("safe", tags: new ActivityTagsCollection { [Secret] = Secret }));
                    break;
                case "empty-event": span.AddEvent(new ActivityEvent("safe")); break;
                case "link-context":
                    span.AddLink(new ActivityLink(new ActivityContext(ActivityTraceId.CreateRandom(),
                        ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded, "vendor=" + Secret)));
                    break;
                case "link-attribute":
                    span.AddLink(new ActivityLink(default, new ActivityTagsCollection { [Secret] = Secret }));
                    break;
                default: span.AddLink(new ActivityLink(default)); break;
            }
        }
        var parentSpan = Assert.Single(fixture.Flush());
        Assert.Equal("agent.run", Text(parentSpan, 5));
        Assert.NotEqual(Convert.FromHexString(droppedId.ToHexString()), Assert.Single(Messages(parentSpan, 2)));
        fixture.AssertWireSafe();
    }

    [Fact]
    public async Task Start_clears_inherited_tracestate_before_wrapper_descendants()
    {
        using var fixture = await Fixture.Start();
        using var native = new ActivitySource(NativeSource);
        using var wrapper = new ActivitySource(AgentTelemetry.SourceName);
        var incoming = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(),
            ActivityTraceFlags.Recorded, "vendor=" + Secret, isRemote: true);
        using (var span = native.StartActivity(Secret, ActivityKind.Internal, incoming))
        {
            Assert.NotNull(span);
            Assert.Null(span.TraceStateString);
            using var child = wrapper.StartActivity("agent.run");
            Assert.NotNull(child);
            Assert.Null(child.TraceStateString);
            Assert.Equal(span.SpanId, child.ParentSpanId);
        }
        Assert.Equal(2, fixture.Flush().Length);
        fixture.AssertWireSafe();
    }

    [Fact]
    public async Task Untrusted_objects_and_invalid_counts_are_omitted_without_string_conversion()
    {
        using var fixture = await Fixture.Start();
        using var native = new ActivitySource(NativeSource);
        object[] invalid = [-1L, -1, "123" + Secret, new ExplosiveValue(), double.NaN,
            double.PositiveInfinity, 1.5d, 1m, true, new[] { Secret }, ulong.MaxValue];
        foreach (var value in invalid)
        {
            using var span = native.StartActivity(Secret);
            Assert.NotNull(span);
            span.SetTag("gen_ai.operation.name", value);
            span.SetTag("gen_ai.request.model", value);
            span.SetTag("gen_ai.agent.name", value);
            span.SetTag("gen_ai.provider.name", value);
            span.SetTag("gen_ai.usage.input_tokens", value);
            span.SetTag("error.type", value);
        }
        using (var span = native.StartActivity(Secret))
        {
            Assert.NotNull(span);
            span.SetTag("gen_ai.operation.name", "chat");
            span.SetTag("gen_ai.provider.name", "openai");
            span.SetTag("gen_ai.usage.input_tokens", 0L);
            span.SetTag("gen_ai.usage.output_tokens", long.MaxValue);
        }
        var spans = fixture.Flush();
        Assert.Equal(invalid.Length + 1, spans.Length);
        foreach (var span in spans.Where(span => Text(span, 5) == "other"))
        {
            Assert.Equal(new Dictionary<string, object>
            {
                ["gen_ai.operation.name"] = "other", ["gen_ai.request.model"] = "other",
                ["gen_ai.agent.name"] = "other", ["gen_ai.provider.name"] = "other"
            }.OrderBy(pair => pair.Key), Tags(span).OrderBy(pair => pair.Key));
        }
        var valid = Tags(Assert.Single(spans, span => Text(span, 5) == "chat"));
        Assert.Equal("openai", valid["gen_ai.provider.name"]);
        Assert.Equal(0L, valid["gen_ai.usage.input_tokens"]);
        Assert.Equal(long.MaxValue, valid["gen_ai.usage.output_tokens"]);
        fixture.AssertWireSafe();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.24.0")]
    [InlineData("1.24.0.0")]
    [InlineData(Secret)]
    public async Task Immutable_scope_metadata_is_allowlisted_or_span_is_dropped(string? version)
    {
        using var fixture = await Fixture.Start();
        using var wrapper = new ActivitySource(AgentTelemetry.SourceName);
        using var native = new ActivitySource(NativeSource, version);
        using (var parent = wrapper.StartActivity("agent.run"))
        using (var span = native.StartActivity(Secret))
        {
            Assert.NotNull(parent);
            Assert.NotNull(span);
            span.SetTag("gen_ai.operation.name", "chat");
        }
        var spans = fixture.Flush();
        Assert.Contains(spans, span => Text(span, 5) == "agent.run");
        Assert.Equal(version == Secret ? 1 : 2, spans.Length);
        fixture.AssertWireSafe();
    }

    [Fact]
    public async Task Immutable_scope_tags_fail_closed_without_leaking_through_exporter_scope()
    {
        using var fixture = await Fixture.Start();
        using var wrapper = new ActivitySource(AgentTelemetry.SourceName);
        using var native = new ActivitySource(NativeSource, version: "1.24.0",
            tags: new[] { new KeyValuePair<string, object?>(Secret, Secret) });
        using (var parent = wrapper.StartActivity("agent.run"))
        using (var span = native.StartActivity(Secret))
        {
            Assert.NotNull(parent);
            Assert.NotNull(span);
            span.SetTag("gen_ai.operation.name", "chat");
        }
        Assert.Equal("agent.run", Text(Assert.Single(fixture.Flush()), 5));
        fixture.AssertWireSafe();
    }

    [Fact]
    public async Task Unsampled_incoming_parent_stays_unrecorded()
    {
        using var fixture = await Fixture.Start();
        using var native = new ActivitySource(NativeSource);
        using var span = native.StartActivity(Secret, ActivityKind.Internal,
            new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(),
                ActivityTraceFlags.None, isRemote: true));
        Assert.True(span is null || !span.Recorded);
        span?.Stop();
        Assert.Empty(fixture.Flush());
        Assert.Empty(fixture.Transport.Requests);
    }

    [Fact]
    public async Task Absent_configuration_does_not_register_native_listener_or_provider()
    {
        using var fixture = await Fixture.Start(enabled: false);
        using var native = new ActivitySource(NativeSource);
        Assert.False(native.HasListeners());
        Assert.Null(native.StartActivity(Secret));
        Assert.Null(fixture.Host.Services.GetService<TracerProvider>());
        Assert.Empty(fixture.Transport.Requests);
    }

    private static Dictionary<string, object> Tags(byte[] span) => Messages(span, 9).ToDictionary(
        tag => Text(tag, 1), tag => AnyValue(Assert.Single(Messages(tag, 2))));

    private sealed class ExplosiveValue
    {
        public override string ToString() => throw new InvalidOperationException("Untrusted ToString must never run.");
    }

    private sealed class Fixture(IHost host, CaptureHandler transport) : IDisposable
    {
        public IHost Host { get; } = host;
        public CaptureHandler Transport { get; } = transport;

        public static async Task<Fixture> Start(bool enabled = true)
        {
            var transport = new CaptureHandler();
            var host = new HostBuilder().ConfigureServices(services =>
            {
                services.AddLogfireTelemetry(new ConfigurationBuilder().AddInMemoryCollection(enabled
                    ? new Dictionary<string, string?>
                    {
                        ["Logfire:Endpoint"] = "https://logfire-eu.pydantic.dev/v1/traces",
                        ["Logfire:Token"] = "pylf_v1_dummy_offline_only"
                    } : []).Build());
                // Reuse the existing sealed in-memory HTTP transport; there is no network fallback.
                services.PostConfigureAll<OtlpExporterOptions>(options => options.HttpClientFactory =
                    () => new HttpClient(transport, disposeHandler: false));
            }).Build();
            await host.StartAsync();
            return new Fixture(host, transport);
        }

        public byte[][] Flush()
        {
            Assert.True(Host.Services.GetRequiredService<TracerProvider>().ForceFlush(10_000));
            return Transport.Requests.SelectMany(request => Messages(request.Body, 1))
                .SelectMany(resource => Messages(resource, 2)).SelectMany(scope => Messages(scope, 2)).ToArray();
        }

        public void AssertWireSafe()
        {
            Assert.NotEmpty(Transport.Requests);
            foreach (var request in Transport.Requests)
            {
                Assert.True(request.RawAuthorizationIsCorrect);
                Assert.Equal("application/x-protobuf", request.ContentType);
                Assert.DoesNotContain(Secret, Encoding.UTF8.GetString(request.Body));
            }
        }

        public void Dispose()
        {
            Host.Dispose();
            Transport.Dispose();
        }
    }
}
