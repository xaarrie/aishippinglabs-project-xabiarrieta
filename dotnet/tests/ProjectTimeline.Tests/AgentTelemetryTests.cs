using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using Xunit;

namespace ProjectTimeline.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AgentTelemetryCollection
{
    public const string Name = "Agent telemetry listeners";
}

[Collection(AgentTelemetryCollection.Name)]
public sealed class AgentTelemetryTests
{
    private const string SourceName = "ProjectTimeline.Agents";
    private const string Secret = "PRIVATE_TELEMETRY_SENTINEL";

    public sealed class Result
    {
        public string? Value { get; set; }
    }

    private sealed class Chat(Func<CancellationToken, Task<ChatResponse>> respond) : IChatClient
    {
        public int Calls;
        public CancellationToken Token;
        public Activity? Current;
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Token = cancellationToken;
            Current = Activity.Current;
            return await respond(cancellationToken);
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class Capture : IDisposable
    {
        private readonly ActivityListener listener;
        public List<Activity> Stopped { get; } = [];
        public Capture(string source = SourceName, ActivitySamplingResult sampling = ActivitySamplingResult.AllDataAndRecorded)
        {
            listener = new ActivityListener
            {
                ShouldListenTo = candidate => candidate.Name == source,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => sampling,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) => sampling,
                ActivityStopped = activity => Stopped.Add(activity)
            };
            ActivitySource.AddActivityListener(listener);
        }
        public void Dispose() => listener.Dispose();
    }

    private static ChatResponse Response(string text = "{\"Value\":\"PRIVATE_TELEMETRY_SENTINEL\"}") =>
        new(new ChatMessage(ChatRole.Assistant, text)) { ResponseId = Secret, ModelId = Secret };

    private static ICustomAgent Create(Chat chat, bool harness = false, string name = "timelineFactExtraction", string model = "gpt-4o-mini",
        List<AITool>? tools = null)
    {
        var provider = new CustomOpenAIClient(chat, isolateHarnessFacilities: true);
        ICustomAgent agent = harness ? new LocalCustomHarnessAgent(provider) : new LocalCustomAgent(provider);
        agent.Init(name, model, Secret, tools ?? []);
        return agent;
    }

    private static StructuredAgentRunOptions Options(Action<ChatResponse>? validate = null, Func<Exception>? invalid = null) => new()
    {
        ChatOptions = new ChatOptions(),
        SerializerOptions = new JsonSerializerOptions(),
        ValidateResponse = validate ?? (_ => { }),
        InvalidResult = invalid ?? (() => new InvalidDataException(Secret))
    };

    private static async Task<AgentResponse> Run(ICustomAgent agent, string kind, StructuredAgentRunOptions? options = null,
        CancellationToken ct = default) => kind switch
    {
        "text" => await agent.RunAsync(Secret),
        "typed" => await agent.RunAsync<Result>(Secret),
        _ => await agent.RunAsync<Result>(Secret, options ?? Options(), ct)
    };

    private static string Payload(Activity activity) => string.Join("|",
        new[] { activity.OperationName, activity.DisplayName, activity.StatusDescription }
            .Concat(activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"))
            .Concat(activity.Events.SelectMany(ev => new[] { ev.Name }.Concat(ev.Tags.Select(tag => $"{tag.Key}={tag.Value}"))))
            .Concat(activity.Links.SelectMany(link => link.Tags?.Select(tag => $"{tag.Key}={tag.Value}") ?? [])));

    private static void Safe(Activity activity)
    {
        Assert.DoesNotContain(Secret, Payload(activity));
        Assert.Null(activity.StatusDescription);
        Assert.Empty(activity.Events);
        Assert.Empty(activity.Links);
        Assert.All(activity.TagObjects, tag => Assert.Contains(tag.Key, new[]
        {
            "gen_ai.agent.name", "gen_ai.request.model", "agent.wrapper", "agent.run.kind", "agent.run.outcome", "error.type"
        }));
    }

    [Theory]
    [InlineData(false, "text")]
    [InlineData(false, "typed")]
    [InlineData(false, "structured")]
    [InlineData(true, "text")]
    [InlineData(true, "typed")]
    public async Task Every_overload_preserves_results_and_records_safe_end_to_end_success(bool harness, string kind)
    {
        using var capture = new Capture();
        using var parent = new Activity("request").Start();
        var release = new TaskCompletionSource<ChatResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chat = new Chat(_ => { entered.SetResult(); return release.Task; });
        var agent = Create(chat, harness);
        var validated = false;
        var run = Run(agent, kind, Options(_ => { validated = true; Assert.Empty(capture.Stopped); }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(capture.Stopped);
        var current = chat.Current;
        release.SetResult(Response());
        var response = await run;
        Assert.Contains(Secret, response.Text);
        if (response is AgentResponse<Result> typed) Assert.Equal(Secret, typed.Result.Value);
        Assert.Equal(kind == "structured", validated);
        Assert.Equal(1, chat.Calls);
        var span = Assert.Single(capture.Stopped);
        Assert.Equal(SourceName, AgentTelemetry.SourceName);
        Assert.Equal(AgentTelemetry.SourceName, span.Source.Name);
        Assert.Same(span, current);
        Assert.Same(parent, Activity.Current);
        Assert.Equal(parent.SpanId, span.ParentSpanId);
        Assert.Equal(parent.TraceId, span.TraceId);
        Assert.Equal(harness ? "agent.harness.run" : "agent.run", span.OperationName);
        Assert.Equal(ActivityKind.Internal, span.Kind);
        Assert.Equal("timelineFactExtraction", span.GetTagItem("gen_ai.agent.name"));
        Assert.Equal("gpt-4o-mini", span.GetTagItem("gen_ai.request.model"));
        Assert.Equal(harness ? "harness" : "local", span.GetTagItem("agent.wrapper"));
        Assert.Equal(kind, span.GetTagItem("agent.run.kind"));
        Assert.Equal("success", span.GetTagItem("agent.run.outcome"));
        Assert.Equal(ActivityStatusCode.Ok, span.Status);
        Assert.True(span.Duration > TimeSpan.Zero);
        Safe(span);
    }

    [Theory]
    [InlineData(false, "text")]
    [InlineData(false, "typed")]
    [InlineData(false, "structured")]
    [InlineData(true, "text")]
    [InlineData(true, "typed")]
    public async Task Every_overload_preserves_exception_identity_without_recording_exception_payload(bool harness, string kind)
    {
        using var capture = new Capture();
        var error = new InvalidOperationException(Secret);
        var agent = Create(new Chat(_ => Task.FromException<ChatResponse>(error)), harness);
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => Run(agent, kind)));
        var span = Assert.Single(capture.Stopped);
        Assert.Equal("error", span.GetTagItem("agent.run.outcome"));
        Assert.Equal(typeof(InvalidOperationException).FullName, span.GetTagItem("error.type"));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Safe(span);
    }

    [Theory]
    [InlineData(false, "text")]
    [InlineData(false, "typed")]
    [InlineData(false, "structured")]
    [InlineData(true, "text")]
    [InlineData(true, "typed")]
    public async Task Provider_cancellation_preserves_exception_identity_in_every_overload(bool harness, string kind)
    {
        using var capture = new Capture();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var error = new OperationCanceledException(Secret, cancel.Token);
        var agent = Create(new Chat(_ => Task.FromException<ChatResponse>(error)), harness);
        Assert.Same(error, await Assert.ThrowsAsync<OperationCanceledException>(() => Run(agent, kind)));
        var span = Assert.Single(capture.Stopped);
        Assert.Equal("cancelled", span.GetTagItem("agent.run.outcome"));
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
        Assert.Null(span.GetTagItem("error.type"));
        Safe(span);
    }

    [Theory]
    [InlineData(false, "text")]
    [InlineData(false, "typed")]
    [InlineData(false, "structured")]
    [InlineData(true, "text")]
    [InlineData(true, "typed")]
    public async Task Uninitialized_runs_keep_the_existing_failure_and_record_unknown_configuration(bool harness, string kind)
    {
        using var capture = new Capture();
        var chat = new Chat(_ => Task.FromResult(Response()));
        var provider = new CustomOpenAIClient(chat, isolateHarnessFacilities: true);
        ICustomAgent agent = harness ? new LocalCustomHarnessAgent(provider) : new LocalCustomAgent(provider);
        var error = await Assert.ThrowsAsync<Exception>(() => Run(agent, kind));
        Assert.Equal("agent cannot be null", error.Message);
        Assert.Equal(0, chat.Calls);
        var span = Assert.Single(capture.Stopped);
        Assert.Equal("unknown", span.GetTagItem("gen_ai.agent.name"));
        Assert.Equal("unknown", span.GetTagItem("gen_ai.request.model"));
        Assert.Equal("error", span.GetTagItem("agent.run.outcome"));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Safe(span);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Structured_validation_and_typed_admission_failures_are_inside_the_run(bool invalidJson)
    {
        using var capture = new Capture();
        var error = new InvalidDataException(Secret);
        var agent = Create(new Chat(_ => Task.FromResult(Response(invalidJson ? "not json " + Secret : "{}"))));
        var options = Options(_ =>
        {
            Assert.Empty(capture.Stopped);
            if (!invalidJson) throw error;
        }, () => error);
        Assert.Same(error, await Assert.ThrowsAsync<InvalidDataException>(() => Run(agent, "structured", options)));
        var span = Assert.Single(capture.Stopped);
        Assert.Equal("error", span.GetTagItem("agent.run.outcome"));
        Assert.Equal(typeof(InvalidDataException).FullName, span.GetTagItem("error.type"));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Safe(span);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("chat")]
    [InlineData("validation")]
    public async Task Structured_cancellation_is_distinct_and_keeps_the_callers_token(string stage)
    {
        using var capture = new Capture();
        using var cancel = new CancellationTokenSource();
        if (stage == "before") cancel.Cancel();
        var chat = new Chat(_ => { if (stage == "chat") cancel.Cancel(); return Task.FromResult(Response()); });
        var agent = Create(chat);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(agent, "structured",
            Options(_ => { if (stage == "validation") cancel.Cancel(); }), cancel.Token));
        Assert.Equal(cancel.Token, error.CancellationToken);
        Assert.Equal(stage == "before" ? 0 : 1, chat.Calls);
        if (stage != "before") Assert.Equal(cancel.Token, chat.Token);
        var span = Assert.Single(capture.Stopped);
        Assert.Equal("cancelled", span.GetTagItem("agent.run.outcome"));
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
        Assert.Null(span.GetTagItem("error.type"));
        Safe(span);
    }

    [Fact]
    public async Task Nested_runs_preserve_ambient_parentage_without_fabricating_tool_spans()
    {
        using var capture = new Capture();
        using var parent = new Activity("request").Start();
        var inner = Create(new Chat(_ => Task.FromResult(Response())));
        var outer = Create(new Chat(async _ => { await inner.RunAsync(Secret); return Response(); }), harness: true);
        await outer.RunAsync(Secret);
        Assert.Equal(2, capture.Stopped.Count);
        var child = capture.Stopped[0];
        var root = capture.Stopped[1];
        Assert.Equal(root.SpanId, child.ParentSpanId);
        Assert.Equal(parent.SpanId, root.ParentSpanId);
        Assert.Equal(parent.TraceId, child.TraceId);
        Assert.Same(parent, Activity.Current);
        Assert.All(capture.Stopped, Safe);
    }

    [Fact]
    public async Task Arbitrary_configuration_and_tool_payloads_are_not_telemetry_metadata()
    {
        using var capture = new Capture();
        var calls = 0;
        var tool = AIFunctionFactory.Create((string input) => input, "safe_tool");
        var chat = new Chat(_ => Task.FromResult(++calls == 1
            ? new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent(Secret, "safe_tool", new Dictionary<string, object?> { ["input"] = Secret })]))
            : Response()));
        var agent = Create(chat, name: Secret, model: Secret, tools: [tool]);
        var notifications = 0;
        agent.FunctionCalled += (_, _) =>
        {
            notifications++;
            Assert.Empty(capture.Stopped);
            Assert.Same(chat.Current, Activity.Current);
        };
        await agent.RunAsync(Secret);
        Assert.True(notifications >= 2);
        Assert.Equal(2, calls);
        var span = Assert.Single(capture.Stopped);
        Assert.Equal("other", span.GetTagItem("gen_ai.agent.name"));
        Assert.Equal("other", span.GetTagItem("gen_ai.request.model"));
        Safe(span);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_listener_or_dropped_sampling_preserves_behavior(bool drop)
    {
        using var capture = drop ? new Capture(sampling: ActivitySamplingResult.None) : null;
        using var parent = new Activity("request").Start();
        var chat = new Chat(_ => Task.FromResult(Response()));
        var agent = Create(chat);
        Assert.Contains(Secret, (await agent.RunAsync(Secret)).Text);
        Assert.Same(parent, chat.Current);
        var error = new InvalidOperationException(Secret);
        var failing = Create(new Chat(_ => Task.FromException<ChatResponse>(error)));
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => failing.RunAsync(Secret)));
        if (capture is not null) Assert.Empty(capture.Stopped);
    }

    [Fact]
    public async Task Installed_native_tracing_leaks_exception_text_even_with_sensitive_data_disabled()
    {
        // Characterization only: never enable this SDK tracing in the production factories.
        const string nativeSource = "ProjectTimeline.Tests.NativePrivacyProbe";
        using var capture = new Capture(nativeSource);
        var error = new InvalidOperationException(Secret);
        var chat = new Chat(_ => Task.FromException<ChatResponse>(error));
        var native = new CustomOpenAIClient(chat).CreateAgent("gpt-4o-mini", "probe", "instructions", [])
            .AsBuilder().UseOpenTelemetry(nativeSource, configure: telemetry => telemetry.EnableSensitiveData = false).Build();
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => native.RunAsync("input")));
        Assert.NotEmpty(capture.Stopped);
        Assert.Contains(capture.Stopped, span => Payload(span).Contains(Secret, StringComparison.Ordinal));
    }
}
