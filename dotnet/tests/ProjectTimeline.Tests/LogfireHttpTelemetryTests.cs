using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
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
public sealed class LogfireHttpTelemetryTests
{
    private const string Secret = "PRIVATE_HTTP_PAYLOAD_SENTINEL";
    private const string AgentContent = "DISCLOSED_AGENT_CONTENT_SENTINEL";
    private const string Trace = "12345678901234567890123456789012";
    private const string Parent = "1234567890123456";

    [Theory]
    [InlineData(null)]
    [InlineData("00-12345678901234567890123456789012-1234567890123456-01")]
    [InlineData("malformed-parent")]
    public async Task Actual_HTTP_records_root_session_and_harness_with_coherent_parentage(string? traceparent)
    {
        using var app = await Fixture.Start();
        await app.Send(traceparent);
        var spans = app.Flush();
        var root = Assert.Single(spans, span => Name(span) == "HTTP request");
        var session = Assert.Single(spans, span => Name(span) == "agent.session");
        var agent = Assert.Single(spans, span => Name(span) == "agent.harness.run");
        var invoke = Assert.Single(spans, span => Name(span) == "invoke_agent");
        var chat = Assert.Single(spans, span => Name(span) == "chat");
        Assert.Equal(5, spans.Length);
        Assert.Equal(Messages(agent, 1).Single(), Messages(invoke, 1).Single());
        Assert.Equal(Messages(agent, 1).Single(), Messages(chat, 1).Single());
        Assert.Equal(Messages(agent, 2).Single(), Messages(invoke, 4).Single());
        Assert.Equal(Messages(invoke, 2).Single(), Messages(chat, 4).Single());
        Assert.True(app.Chat.Recorded);
        Assert.Equal(Messages(root, 1).Single(), Messages(session, 1).Single());
        Assert.Equal(Messages(root, 1).Single(), Messages(agent, 1).Single());
        Assert.Equal(Messages(root, 2).Single(), Messages(session, 4).Single());
        Assert.Equal(Messages(session, 2).Single(), Messages(agent, 4).Single());
        Assert.Equal("POST", Tags(root)["http.request.method"]);
        Assert.Equal("200", Tags(root)["http.response.status_code"]);
        if (traceparent?.EndsWith("-01") == true)
        {
            Assert.Equal(Trace, Convert.ToHexString(Messages(root, 1).Single()).ToLowerInvariant());
            Assert.Equal(Parent, Convert.ToHexString(Messages(root, 4).Single()).ToLowerInvariant());
        }
        else Assert.Empty(Messages(root, 4));
        AssertSafe(app, spans);
    }

    [Fact]
    public async Task Incoming_unsampled_HTTP_parent_exports_nothing_including_session()
    {
        using var app = await Fixture.Start();
        await app.Send($"00-{Trace}-{Parent}-00");
        Assert.Equal(1, app.Chat.Calls);
        Assert.False(app.Chat.Recorded);
        Assert.Empty(app.Flush());
        Assert.Empty(app.Transport.Bodies);
    }

    [Fact]
    public async Task No_configuration_leaves_HTTP_and_agent_unexported()
    {
        using var app = await Fixture.Start(enabled: false);
        await app.Send();
        Assert.Equal(1, app.Chat.Calls);
        Assert.Null(app.Host.Services.GetService<TracerProvider>());
        Assert.Empty(app.Transport.Bodies);
    }

    [Theory]
    [InlineData("normal", false)]
    [InlineData("error", false)]
    [InlineData("event", false)]
    [InlineData("link", false)]
    [InlineData("method", false)]
    [InlineData("normal", true)]
    [InlineData("error", true)]
    [InlineData("event", true)]
    [InlineData("link", true)]
    [InlineData("method", true)]
    public async Task Final_OTLP_excludes_all_request_payloads_and_contaminated_HTTP_data(string scenario, bool capture)
    {
        using var app = await Fixture.Start(scenario: scenario, capture: capture);
        if (scenario == "error")
            await Assert.ThrowsAsync<InvalidOperationException>(() => app.Send());
        else await app.Send();
        var spans = app.Flush();
        Assert.Contains(spans, span => Name(span) == "agent.harness.run");
        Assert.Contains(spans, span => Name(span) == "agent.session");
        if (scenario is "event" or "link")
            Assert.DoesNotContain(spans, span => Name(span) == "HTTP request");
        else
        {
            var root = Assert.Single(spans, span => Name(span) == "HTTP request");
            Assert.Equal(scenario == "method" ? "_OTHER" : "POST", Tags(root)["http.request.method"]);
            Assert.Equal(scenario == "error" ? "500" : "200", Tags(root)["http.response.status_code"]);
        }
        AssertSafe(app, spans);
    }

    private static string Name(byte[] span) => Text(span, 5);

    private static Dictionary<string, string> Tags(byte[] span) => Messages(span, 9).ToDictionary(
        tag => Text(tag, 1), tag =>
        {
            var value = Messages(tag, 2).Single();
            // These allowlisted values are either OTLP string or small integer values.
            return value[0] == 10 ? Text(value, 1) : ReadInteger(value).ToString();
        });

    private static int ReadInteger(byte[] value)
    {
        Assert.Equal(24, value[0]);
        var result = 0;
        for (var i = 1; i < value.Length; i++) result |= (value[i] & 127) << (7 * (i - 1));
        return result;
    }

    private static void AssertSafe(Fixture app, byte[][] spans)
    {
        Assert.NotEmpty(app.Transport.Bodies);
        foreach (var body in app.Transport.Bodies)
        {
            Assert.DoesNotContain(Secret, Encoding.UTF8.GetString(body));
            if (!app.Capture) Assert.DoesNotContain(AgentContent, Encoding.UTF8.GetString(body));
        }
        foreach (var span in spans)
        {
            Assert.Empty(Messages(span, 3)); // No untrusted tracestate header.
            Assert.Empty(Messages(span, 11)); // Events, including exception details.
            Assert.Empty(Messages(span, 13)); // Links can carry untrusted attributes too.
            foreach (var status in Messages(span, 15)) Assert.Empty(Messages(status, 2)); // Description.
            Assert.Contains(Name(span), new[] { "HTTP request", "agent.session", "agent.harness.run", "invoke_agent", "chat" });
            if (Name(span) is "invoke_agent" or "chat")
            {
                var tags = Tags(span);
                Assert.Equal(Name(span), tags["gen_ai.operation.name"]);
                Assert.All(tags.Keys, key => Assert.Contains(key, app.Capture ? new[]
                {
                    "gen_ai.operation.name", "gen_ai.agent.name", "gen_ai.agent.id", "gen_ai.request.model",
                    "gen_ai.response.model", "gen_ai.provider.name", "gen_ai.usage.input_tokens", "gen_ai.usage.output_tokens",
                    "gen_ai.input.messages", "gen_ai.output.messages", "gen_ai.system_instructions", "gen_ai.tool.definitions"
                } : new[]
                {
                    "gen_ai.operation.name", "gen_ai.agent.name", "gen_ai.request.model", "gen_ai.response.model",
                    "gen_ai.provider.name", "gen_ai.usage.input_tokens", "gen_ai.usage.output_tokens"
                }));
                if (tags.TryGetValue("gen_ai.response.model", out var model)) Assert.Equal(app.Capture ? AgentContent : "other", model);
            }
            if (Name(span) == "HTTP request")
                Assert.All(Tags(span).Keys, key => Assert.Contains(key,
                    new[] { "http.request.method", "http.response.status_code" }));
        }
    }

    private sealed class Fixture(IHost host, Transport transport, Chat chat, ActivityListener hostingListener,
        TaskCompletionSource stopped) : IDisposable
    {
        public IHost Host { get; } = host;
        public Transport Transport { get; } = transport;
        public Chat Chat { get; } = chat;
        public bool Capture { get; init; }

        public static async Task<Fixture> Start(bool enabled = true, string scenario = "normal", bool capture = false)
        {
            // Model the real host's local, unrecorded HTTP activity without sampling it on.
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostingListener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.PropagationData,
                ActivityStopped = _ => stopped.TrySetResult()
            };
            var transport = new Transport();
            var chat = new Chat();
            var agent = new LocalCustomHarnessAgent(new CustomOpenAIClient(chat, isolateHarnessFacilities: true,
                telemetryOptions: new(capture)));
            agent.Init("timelineFactExtraction", "gpt-4o-mini", AgentContent, []);
            var host = new HostBuilder().ConfigureWebHost(web => web.UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddLogfireTelemetry(new ConfigurationBuilder().AddInMemoryCollection(
                        enabled ? new Dictionary<string, string?>
                        {
                            ["Logfire:Endpoint"] = "https://logfire-eu.pydantic.dev/v1/traces",
                            ["Logfire:Token"] = "dummy_offline_only",
                            ["Logfire:CaptureAgentContent"] = capture.ToString()
                        } : []).Build());
                    // Sealed transport: every exporter request terminates in memory, never on the network.
                    services.PostConfigureAll<OtlpExporterOptions>(options =>
                        options.HttpClientFactory = () => new HttpClient(transport, disposeHandler: false));
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.Map("/operation/{id}", async context =>
                    {
                        Assert.Contains(Secret, await new StreamReader(context.Request.Body).ReadToEndAsync());
                        var root = Activity.Current!;
                        root.DisplayName = Secret;
                        root.SetTag(Secret, Secret);
                        root.SetTag("http.route", Secret); // Never trust mutable route tags.
                        root.SetTag("http.request.method", Secret);
                        root.SetStatus(ActivityStatusCode.Error, Secret);
                        if (scenario == "event") root.AddEvent(new ActivityEvent(Secret,
                            tags: new ActivityTagsCollection { [Secret] = Secret }));
                        if (scenario == "link") root.AddLink(new ActivityLink(default,
                            new ActivityTagsCollection { [Secret] = Secret }));
                        await agent.RunAsync(AgentContent); // Ordinary wrapper owns the session beneath HTTP.
                        if (scenario == "error")
                        {
                            context.Response.StatusCode = 500;
                            throw new InvalidOperationException(Secret);
                        }
                        context.Response.StatusCode = 200;
                    }));
                })).Build();
            await host.StartAsync();
            // Register after the provider so completion follows its processor/exporter callbacks.
            ActivitySource.AddActivityListener(hostingListener);
            return new Fixture(host, transport, chat, hostingListener, stopped) { Scenario = scenario, Capture = capture };
        }

        private string Scenario { get; init; } = "normal";

        public async Task Send(string? traceparent = null)
        {
            using var client = Host.GetTestClient();
            using var request = new HttpRequestMessage(Scenario == "method" ? new HttpMethod(Secret) : HttpMethod.Post,
                $"/operation/{Secret}?{Secret}={Secret}") { Content = new StringContent(Secret) };
            request.Headers.Add("X-Private", Secret);
            request.Headers.Add("Authorization", $"Bearer {Secret}");
            request.Headers.Add("tracestate", $"vendor={Secret}");
            request.Headers.Add("baggage", $"private={Secret}");
            request.Headers.Add("User-Agent", Secret);
            request.Headers.Host = $"{Secret}.invalid";
            if (traceparent is not null) request.Headers.Add("traceparent", traceparent);
            try
            {
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            finally
            {
                // TestServer can finish the response (or throw) before stopping the HTTP activity.
                await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        public byte[][] Flush()
        {
            Assert.True(Host.Services.GetRequiredService<TracerProvider>().ForceFlush(10_000));
            return Transport.Bodies.SelectMany(body => Messages(body, 1))
                .SelectMany(resource => Messages(resource, 2)).SelectMany(scope => Messages(scope, 2)).ToArray();
        }

        public void Dispose()
        {
            Host.Dispose();
            hostingListener.Dispose();
            Transport.Dispose();
        }
    }

    private sealed class Chat : IChatClient
    {
        public int Calls;
        public bool Recorded;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Recorded = Activity.Current?.Recorded == true;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, AgentContent))
                { ModelId = AgentContent, Usage = new() { InputTokenCount = 1, OutputTokenCount = 1, TotalTokenCount = 2 } });
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class Transport : HttpMessageHandler
    {
        public ConcurrentQueue<byte[]> Bodies { get; } = new();
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            SendAsync(request, cancellationToken).GetAwaiter().GetResult();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Enqueue(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        }
    }
}
