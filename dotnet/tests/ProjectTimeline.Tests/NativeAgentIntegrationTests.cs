using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
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
public sealed class NativeAgentIntegrationTests
{
    private const string Secret = "PRIVATE_NATIVE_INTEGRATION_SENTINEL";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Factories_emit_one_agent_and_one_chat_per_provider_turn_with_real_tool_spans(bool harness)
    {
        using var capture = new Capture();
        using var parent = new Activity("offline-parent").Start();
        var toolCalls = 0;
        Activity? toolActivity = null;
        var tool = AIFunctionFactory.Create((string input) =>
        {
            Assert.Equal(Secret, input);
            toolCalls++;
            toolActivity = Activity.Current;
            return Secret;
        }, Secret, Secret);
        var chat = new Chat(call => Response(tool: call == 1));
        var provider = new CustomOpenAIClient(chat, isolateHarnessFacilities: true);
        Assert.Same(chat, provider.GetChatClient(Secret));
        var agent = harness ? provider.CreateHarnessAgent(Secret, Secret, Secret, [tool])
            : provider.CreateAgent(Secret, Secret, Secret, [tool]);
        using var session = AgentCostSession.Start(new Dictionary<string, AgentTokenPricing> { [Secret] = new(2m, 5m) });
        var sessionActivity = Activity.Current;
        Assert.Contains(Secret, (await agent.RunAsync(Secret)).Text);
        Assert.Equal(2, chat.Calls);
        Assert.Equal(1, toolCalls);
        Assert.Equal(2, session.GetSnapshot().Totals.CallCount);
        Assert.Equal(0.0006m, session.GetSnapshot().Totals.EstimatedTotalCost);
        Assert.Equal(4, capture.Stopped.Count);
        var invoke = Assert.Single(capture.Stopped, span => Operation(span) == "invoke_agent");
        var chats = capture.Stopped.Where(span => Operation(span) == "chat").ToArray();
        Assert.Equal(chat.Calls, chats.Length);
        var execution = Assert.Single(capture.Stopped, span => Operation(span) == "execute_tool");
        Assert.Same(execution, toolActivity);
        Assert.Equal(sessionActivity!.SpanId, invoke.ParentSpanId);
        Assert.All(chats.Append(execution), span => Assert.Equal(invoke.SpanId, span.ParentSpanId));
        Assert.All(chat.Activities, span => Assert.Contains(span, chats));
        Assert.All(capture.Stopped, span =>
        {
            Assert.Equal(AgentTelemetry.NativeSourceName, span.Source.Name);
            Assert.Equal("", span.Source.Version);
            Assert.Empty(span.Source.Tags ?? []);
        });
        Assert.Same(sessionActivity, Activity.Current);
    }

    [Theory]
    [InlineData("text", false)]
    [InlineData("typed", false)]
    [InlineData("structured", false)]
    [InlineData("text", true)]
    [InlineData("typed", true)]
    [InlineData("direct", false)]
    [InlineData("direct", true)]
    [InlineData("judge", false)]
    public async Task Actual_host_pipeline_exports_safe_native_hierarchy_and_exact_costs(string kind, bool harness)
    {
        using var wire = await Wire.Start();
        var toolCalls = 0;
        var tool = AIFunctionFactory.Create((string input) =>
        {
            Assert.Equal(Secret, input);
            toolCalls++;
            return Secret;
        }, Secret, Secret);
        var chat = new Chat(call => Response(tool: kind != "judge" && call == 1));
        var provider = new CustomOpenAIClient(chat, true, new(false));
        var session = AgentCostSession.Start(Prices());
        var sessionId = Activity.Current!.SpanId;
        using (session) await Run(provider, kind, harness, [tool]);
        Assert.Equal(kind == "judge" ? 0 : 1, toolCalls);
        Assert.Equal(kind == "judge" ? 1 : 2, chat.Calls);
        Assert.Equal(chat.Calls, session.GetSnapshot().Totals.CallCount);
        Assert.Equal(chat.Calls * 0.0003m, session.GetSnapshot().Totals.EstimatedTotalCost);
        var spans = wire.Flush();
        var invoke = Assert.Single(spans, span => Name(span) == "invoke_agent");
        var chats = spans.Where(span => Name(span) == "chat").ToArray();
        Assert.Equal(chat.Calls, chats.Length);
        var tools = spans.Where(span => Name(span) == "execute_tool").ToArray();
        Assert.Equal(toolCalls, tools.Length);
        var summary = Assert.Single(spans, span => Name(span) == "agent.session");
        Assert.Equal((long)chat.Calls, Tags(summary)["agent.session.call_count"]);
        Assert.Equal(chat.Calls * 100L, Tags(summary)["agent.session.input_tokens"]);
        Assert.Equal(chat.Calls * 20L, Tags(summary)["agent.session.output_tokens"]);
        Assert.Equal((double)(chat.Calls * 0.0003m), Tags(summary)["agent.session.estimated_total_cost"]);
        var run = spans.SingleOrDefault(span => Name(span) is "agent.run" or "agent.harness.run");
        Assert.Equal(kind is "direct" or "judge", run is null);
        Assert.Equal(run is null ? Convert.FromHexString(sessionId.ToHexString()) : Id(run), Parent(invoke));
        if (run is not null) Assert.Equal(Id(summary), Parent(run));
        Assert.All(chats.Concat(tools), span => Assert.Equal(Id(invoke), Parent(span)));
        Assert.All(spans, span => Assert.Equal(Messages(summary, 1).Single(), Messages(span, 1).Single()));
        Assert.Equal(2 + (run is null ? 0 : 1) + chat.Calls + toolCalls, spans.Length);
        Assert.Equal("other", Tags(invoke)["gen_ai.agent.name"]);
        Assert.All(chats, span =>
        {
            Assert.Equal("other", Tags(span)["gen_ai.response.model"]);
            Assert.Equal(100L, Tags(span)["gen_ai.usage.input_tokens"]);
            Assert.Equal(20L, Tags(span)["gen_ai.usage.output_tokens"]);
        });
        wire.AssertSafe(spans);
    }

    [Theory]
    [InlineData(false, "error")]
    [InlineData(true, "error")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "validation")]
    [InlineData(false, "before")]
    public async Task Native_failure_text_is_sanitized_while_wrapper_semantics_and_costs_survive(bool harness, string stage)
    {
        using var wire = await Wire.Start();
        using var cancel = new CancellationTokenSource();
        Exception error = stage is "cancel" or "before" ? new OperationCanceledException(Secret, cancel.Token)
            : new InvalidDataException(Secret);
        var chat = new Chat(_ => stage is "error" or "cancel" ? throw error : Response());
        var provider = new CustomOpenAIClient(chat, true, new(false));
        ICustomAgent agent = harness ? new LocalCustomHarnessAgent(provider) : new LocalCustomAgent(provider);
        agent.Init(Secret, Secret, Secret, []);
        if (stage == "before") cancel.Cancel();
        var session = AgentCostSession.Start(Prices());
        using (session)
        {
            var actual = await Record.ExceptionAsync(() => harness ? agent.RunAsync(Secret)
                : agent.RunAsync<Result>(Secret, Options(_ => { if (stage == "validation") throw error; }), cancel.Token));
            if (stage == "before") Assert.IsAssignableFrom<OperationCanceledException>(actual);
            else Assert.Same(error, actual);
        }
        Assert.Equal(stage == "before" ? 0 : 1, chat.Calls);
        var totals = session.GetSnapshot().Totals;
        Assert.Equal(chat.Calls, totals.CallCount);
        Assert.Equal(stage is "error" or "cancel" ? 1 : 0, totals.UnknownUsageCallCount);
        if (stage == "validation") Assert.Equal(0.0003m, totals.EstimatedTotalCost);
        var spans = wire.Flush();
        var wrapper = Assert.Single(spans, span => Name(span) is "agent.run" or "agent.harness.run");
        Assert.Equal(stage is "cancel" or "before" ? "cancelled" : "error", Tags(wrapper)["agent.run.outcome"]);
        Assert.Contains(spans, span => Name(span) == "agent.session");
        // This SDK puts exception text in mutable tags/status, not events: safe native spans survive.
        // Structured validation runs outside the native invocation; the wrapper owns that failure.
        Assert.Equal(stage == "before" ? 0 : 1, spans.Count(span => Name(span) == "invoke_agent"));
        Assert.Equal(chat.Calls, spans.Count(span => Name(span) == "chat"));
        wire.AssertSafe(spans);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Contaminated_real_native_chat_or_tool_is_dropped_without_losing_safe_wrapper(
        bool harness, bool inTool, bool link)
    {
        using var wire = await Wire.Start();
        void Contaminate()
        {
            var activity = Activity.Current!;
            Assert.Equal(AgentTelemetry.NativeSourceName, activity.Source.Name);
            Assert.Equal(inTool ? "execute_tool" : "chat", Operation(activity));
            if (link) activity.AddLink(new ActivityLink(default, new ActivityTagsCollection { [Secret] = Secret }));
            else activity.AddEvent(new ActivityEvent(Secret, tags: new ActivityTagsCollection { [Secret] = Secret }));
        }
        var tool = AIFunctionFactory.Create((string input) => { Contaminate(); return input; }, Secret, Secret);
        var chat = new Chat(call =>
        {
            if (!inTool && call == 1) Contaminate();
            return Response(tool: call == 1);
        });
        var provider = new CustomOpenAIClient(chat, true, new(false));
        ICustomAgent agent = harness ? new LocalCustomHarnessAgent(provider) : new LocalCustomAgent(provider);
        agent.Init(Secret, Secret, Secret, [tool]);
        var error = new InvalidDataException(Secret);
        using (AgentCostSession.Start(Prices()))
        {
            if (harness) await agent.RunAsync(Secret);
            else Assert.Same(error, await Assert.ThrowsAsync<InvalidDataException>(() =>
                agent.RunAsync<Result>(Secret, Options(_ => throw error), default)));
        }
        var spans = wire.Flush();
        Assert.Single(spans, span => Name(span) is "agent.run" or "agent.harness.run");
        Assert.Single(spans, span => Name(span) == "invoke_agent");
        Assert.Equal(inTool ? 2 : 1, spans.Count(span => Name(span) == "chat"));
        Assert.Equal(inTool ? 0 : 1, spans.Count(span => Name(span) == "execute_tool"));
        Assert.Equal(2, chat.Calls);
        wire.AssertSafe(spans);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Shared_factory_native_parentage_and_accounting_are_isolated_concurrently(bool harness, bool explicitScope)
    {
        using var wire = await Wire.Start();
        var chat = new Chat(_ => Response());
        var provider = new CustomOpenAIClient(chat, true, new(false));
        ICustomAgent agent = harness ? new LocalCustomHarnessAgent(provider) : new LocalCustomAgent(provider);
        agent.Init(Secret, Secret, Secret, []);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            using var session = explicitScope ? AgentCostSession.Start(Prices()) : null;
            await Task.Yield();
            await agent.RunAsync(Secret);
            Assert.Same(session, AgentCostSession.Current);
            if (session is not null)
            {
                Assert.False(session.GetSnapshot().IsDisposed);
                Assert.Equal(1, session.GetSnapshot().Totals.CallCount);
                Assert.Equal(0.0003m, session.GetSnapshot().Totals.EstimatedTotalCost);
            }
        }));
        Assert.Equal(4, chat.Calls);
        var spans = wire.Flush();
        Assert.Equal(16, spans.Length);
        var sessions = spans.Where(span => Name(span) == "agent.session").ToArray();
        Assert.Equal(4, sessions.Length);
        Assert.Equal(4, sessions.Select(span => Convert.ToHexString(Messages(span, 1).Single())).Distinct().Count());
        foreach (var session in sessions)
        {
            var run = Assert.Single(spans, span => Parent(span).SequenceEqual(Id(session)));
            Assert.Equal(harness ? "agent.harness.run" : "agent.run", Name(run));
            var invoke = Assert.Single(spans, span => Parent(span).SequenceEqual(Id(run)));
            Assert.Equal("invoke_agent", Name(invoke));
            Assert.Equal("chat", Name(Assert.Single(spans, span => Parent(span).SequenceEqual(Id(invoke)))));
        }
        wire.AssertSafe(spans);
        Assert.Null(AgentCostSession.Current);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task No_configuration_or_unsampled_parent_preserves_results_and_costs_without_export(bool harness, bool enabled)
    {
        using var wire = await Wire.Start(enabled);
        using var parentSource = new ActivitySource("ProjectTimeline.Tests.NativeUnrecordedParent");
        using var parentListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == parentSource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.PropagationData
        };
        ActivitySource.AddActivityListener(parentListener);
        using var parent = parentSource.StartActivity("offline-unsampled");
        var chat = new Chat(_ => Response());
        var provider = new CustomOpenAIClient(chat, true);
        var session = AgentCostSession.Start(Prices());
        using (session) await Run(provider, "direct", harness, []);
        Assert.Equal(1, chat.Calls);
        Assert.Equal(1, session.GetSnapshot().Totals.CallCount);
        Assert.Equal(0.0003m, session.GetSnapshot().Totals.EstimatedTotalCost);
        Assert.All(chat.Activities, span => Assert.True(span is null || !span.Recorded));
        if (!enabled) Assert.All(chat.Activities, span => Assert.Same(parent, span));
        Assert.Empty(wire.Flush());
        Assert.Empty(wire.Transport.Requests);
        Assert.Same(parent, Activity.Current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_capture_key_characterization(bool harness)
    {
        using var capture = new Capture();
        var tool = AIFunctionFactory.Create((string input) => Secret, Secret, Secret);
        var provider = new CustomOpenAIClient(new Chat(call => Response(call == 1)), true);
        var agent = harness ? provider.CreateHarnessAgent(Secret, Secret, Secret, [tool])
            : provider.CreateAgent(Secret, Secret, Secret, [tool]);
        await agent.RunAsync(Secret);
        foreach (var key in ContentKeys)
            Assert.Contains(capture.Stopped, span => span.GetTagItem(key) is string);
        Assert.All(capture.Stopped, span => { Assert.Empty(span.Events); Assert.Empty(span.Links); });
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task Default_capture_exports_native_conversation_and_tool_content(bool harness, bool? captureSetting)
    {
        var tool = AIFunctionFactory.Create((string input) => "RESULT_SENTINEL", "lookup_fact", "TOOL_DEFINITION_SENTINEL");
        var chat = new Chat(call => call % 2 == 1
            ? new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("CALL_ID_SENTINEL", "lookup_fact", new Dictionary<string, object?> { ["input"] = "ARGUMENT_SENTINEL" })]))
                { ModelId = "gpt-4o-mini", ResponseId = "RESPONSE_ID_SENTINEL", Usage = new() { InputTokenCount = 100, OutputTokenCount = 20 } }
            : new ChatResponse(new ChatMessage(ChatRole.Assistant, "ANSWER_SENTINEL"))
                { ModelId = "gpt-4o-mini", ResponseId = "RESPONSE_ID_SENTINEL", Usage = new() { InputTokenCount = 100, OutputTokenCount = 20 } });
        var provider = new CustomOpenAIClient(chat, isolateHarnessFacilities: true,
            telemetryOptions: captureSetting.HasValue ? new(captureSetting.Value) : null);
        Assert.Same(chat, provider.GetChatClient("gpt-4o-mini"));
        var agent = harness ? provider.CreateHarnessAgent("gpt-4o-mini", "timelineFactInformation", "INSTRUCTIONS_SENTINEL", [tool])
            : provider.CreateAgent("gpt-4o-mini", "timelineFactInformation", "INSTRUCTIONS_SENTINEL", [tool]);
        Activity[] raw;
        using (var capture = new Capture())
        {
            await agent.RunAsync("QUESTION_SENTINEL");
            raw = capture.Stopped.ToArray();
        }
        using var wire = await Wire.Start(capture: captureSetting);
        var session = AgentCostSession.Start(new Dictionary<string, AgentTokenPricing> { ["gpt-4o-mini"] = new(2m, 5m) });
        using (session) Assert.Equal("ANSWER_SENTINEL", (await agent.RunAsync("QUESTION_SENTINEL")).Text);
        Assert.Equal(4, chat.Calls); // Two turns per independent raw/wire run, no duplicates.
        Assert.Equal(2, session.GetSnapshot().Totals.CallCount);
        Assert.Equal(0.0006m, session.GetSnapshot().Totals.EstimatedTotalCost);
        var spans = wire.Flush();
        Assert.Equal(5, spans.Length); // session + invocation + two provider turns + tool
        var allTags = string.Join("\n", spans.SelectMany(span => Tags(span)).Select(tag => tag.Key + "=" + tag.Value));
        foreach (var expected in new[] { "QUESTION_SENTINEL", "ANSWER_SENTINEL", "ARGUMENT_SENTINEL", "RESULT_SENTINEL",
            "INSTRUCTIONS_SENTINEL", "TOOL_DEFINITION_SENTINEL", "CALL_ID_SENTINEL", "RESPONSE_ID_SENTINEL", "lookup_fact" })
            if (captureSetting != false) Assert.Contains(expected, allTags);
            else Assert.DoesNotContain(expected, allTags);
        if (captureSetting != false)
        {
            var exported = spans.Where(span => Name(span) != "agent.session").ToArray();
            Assert.Equal(raw.Length, exported.Length);
            for (var i = 0; i < raw.Length; i++)
                foreach (var tag in raw[i].TagObjects.Where(tag => ContentKeys.Contains(tag.Key)))
                    Assert.Equal(tag.Value, Tags(exported[i])[tag.Key]); // Full native value, not reconstructed/truncated.
        }
        Assert.All(spans, span =>
        {
            Assert.Empty(Messages(span, 11));
            Assert.Empty(Messages(span, 13));
        });
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task Content_policy_never_admits_exception_or_arbitrary_metadata(bool harness, bool capture, bool cancel)
    {
        using var wire = await Wire.Start(capture: capture);
        Exception failure = cancel ? new OperationCanceledException(Secret) : new InvalidDataException(Secret);
        var chat = new Chat(_ =>
        {
            Activity.Current!.SetTag("gen_ai.unvetted", Secret);
            Activity.Current.SetTag("http.request.header.authorization", Secret);
            throw failure;
        });
        var provider = new CustomOpenAIClient(chat, true, new(capture));
        ICustomAgent agent = harness ? new LocalCustomHarnessAgent(provider) : new LocalCustomAgent(provider);
        agent.Init("timelineFactInformation", "gpt-4o-mini", "Public instructions", []);
        var session = AgentCostSession.Start();
        using (session) Assert.Same(failure, await Record.ExceptionAsync(() => agent.RunAsync("Public question")));
        Assert.Equal(1, chat.Calls);
        Assert.Equal(1, session.GetSnapshot().Totals.UnknownUsageCallCount);
        var spans = wire.Flush();
        Assert.Equal(4, spans.Length);
        Assert.All(wire.Transport.Requests, request => Assert.DoesNotContain(Secret, Encoding.UTF8.GetString(request.Body)));
        Assert.All(spans, span =>
        {
            Assert.Empty(Messages(span, 11));
            Assert.Empty(Messages(span, 13));
            foreach (var status in Messages(span, 15)) Assert.Empty(Messages(status, 2));
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Tool_failures_do_not_export_exception_details_in_either_mode(bool harness, bool capture)
    {
        using var wire = await Wire.Start(capture: capture);
        var toolCalls = 0;
        var tool = AIFunctionFactory.Create(new Func<string, string>(input =>
        {
            toolCalls++;
            throw new InvalidDataException(Secret);
        }), "failing_tool");
        var chat = new Chat(turn => new ChatResponse(new ChatMessage(ChatRole.Assistant, turn == 1
            ? [new FunctionCallContent("public-call", "failing_tool", new Dictionary<string, object?> { ["input"] = "public" })]
            : [new TextContent("Public final answer")]))
            { ModelId = "gpt-4o-mini", Usage = new() { InputTokenCount = 1, OutputTokenCount = 1 } });
        var provider = new CustomOpenAIClient(chat, true, new(capture));
        var agent = harness ? provider.CreateHarnessAgent("gpt-4o-mini", "timelineFactInformation", "Public instructions", [tool])
            : provider.CreateAgent("gpt-4o-mini", "timelineFactInformation", "Public instructions", [tool]);
        using (AgentCostSession.Start()) Assert.Equal("Public final answer", (await agent.RunAsync("Public question")).Text);
        Assert.Equal(2, chat.Calls);
        Assert.Equal(1, toolCalls);
        Assert.Equal(5, wire.Flush().Length);
        Assert.All(wire.Transport.Requests, request => Assert.DoesNotContain(Secret, Encoding.UTF8.GetString(request.Body)));
    }

    [Theory]
    [InlineData(false, "text", "known")]
    [InlineData(true, "text", "known")]
    [InlineData(false, "typed", "known")]
    [InlineData(true, "typed", "known")]
    [InlineData(false, "structured", "known")]
    [InlineData(false, "text", "cache")]
    [InlineData(true, "text", "unknown")]
    [InlineData(false, "text", "error")]
    [InlineData(true, "text", "cancel")]
    public async Task Ordinary_wrappers_export_owned_default_costs_through_final_OTLP(bool harness, string kind, string scenario)
    {
        using var wire = await Wire.Start();
        using var parent = new Activity("offline-parent").Start();
        parent.ActivityTraceFlags = ActivityTraceFlags.Recorded; // Model an already-sampled inbound operation.
        Assert.Null(AgentCostSession.Current);
        AgentCostSession? owned = null;
        var toolCalls = 0;
        var tool = AIFunctionFactory.Create((string input) => { toolCalls++; return input; }, Secret);
        Exception failure = scenario == "cancel" ? new OperationCanceledException(Secret) : new InvalidDataException(Secret);
        var chat = new Chat(turn =>
        {
            owned ??= AgentCostSession.Current;
            Assert.Same(owned, AgentCostSession.Current);
            if (scenario is "error" or "cancel") throw failure;
            var response = Response(tool: turn <= 2);
            response.ModelId = scenario == "unknown" && turn == 3 ? Secret : "gpt-4o-mini";
            response.Usage!.CachedInputTokenCount = scenario == "cache" ? null : 0;
            return response;
        });
        var provider = new CustomOpenAIClient(chat, true, new(false));
        ICustomAgent agent = harness ? new LocalCustomHarnessAgent(provider) : new LocalCustomAgent(provider);
        agent.Init(Secret, "gpt-4o-mini", Secret, [tool]);
        async Task RunWrapper()
        {
            if (kind == "structured") await agent.RunAsync<Result>(Secret, Options(), default);
            else if (kind == "typed") await agent.RunAsync<Result>(Secret);
            else await agent.RunAsync(Secret);
        }
        if (scenario is "error" or "cancel") Assert.Same(failure, await Record.ExceptionAsync(RunWrapper));
        else await RunWrapper();
        Assert.NotNull(owned);
        Assert.True(owned.GetSnapshot().IsDisposed);
        Assert.Null(AgentCostSession.Current);
        Assert.Same(parent, Activity.Current);
        var spans = wire.Flush();
        var summary = Assert.Single(spans, span => Name(span) == "agent.session");
        var wrapper = Assert.Single(spans, span => Name(span) is "agent.run" or "agent.harness.run");
        var invoke = Assert.Single(spans, span => Name(span) == "invoke_agent");
        Assert.Equal(Convert.FromHexString(parent.SpanId.ToHexString()), Parent(summary));
        Assert.Equal(Id(summary), Parent(wrapper));
        Assert.Equal(Id(wrapper), Parent(invoke));
        Assert.All(spans.Where(span => Name(span) is "chat" or "execute_tool"),
            span => Assert.Equal(Id(invoke), Parent(span)));
        var failed = scenario is "error" or "cancel";
        Assert.Equal(failed ? 1 : 3, chat.Calls);
        Assert.Equal(failed ? 0 : 2, toolCalls);
        Assert.Equal(chat.Calls, spans.Count(span => Name(span) == "chat"));
        Assert.Equal(toolCalls, spans.Count(span => Name(span) == "execute_tool"));
        Assert.Equal(3 + chat.Calls + toolCalls, spans.Length);
        var tags = Tags(summary); // Decode protobuf AnyValue, including double/bool, after real processors/exporter.
        Assert.Equal("USD", tags["agent.session.currency"]);
        Assert.Equal((long)chat.Calls, tags["agent.session.call_count"]);
        Assert.Equal(0L, tags["agent.session.in_flight_call_count"]);
        Assert.Equal(failed ? 1L : 0L, tags["agent.session.unknown_usage_call_count"]);
        Assert.Equal(scenario == "known", tags["agent.session.estimated_cost_complete"]);
        Assert.Equal(scenario == "known" ? 0L : scenario == "unknown" ? 1L : chat.Calls,
            tags["agent.session.unknown_cost_call_count"]);
        var known = scenario switch { "known" => 0.000081m, "cache" => 0.000036m, "unknown" => 0.000054m, _ => 0m };
        Assert.Equal((double)known, Assert.IsType<double>(tags["agent.session.known_estimated_cost"]));
        if (scenario == "known")
        {
            Assert.Equal(300L, tags["agent.session.input_tokens"]);
            Assert.Equal(60L, tags["agent.session.output_tokens"]);
            Assert.Equal(360L, tags["agent.session.total_tokens"]);
            Assert.Equal((double)known, Assert.IsType<double>(tags["agent.session.estimated_total_cost"]));
        }
        else Assert.DoesNotContain("agent.session.estimated_total_cost", tags.Keys);
        wire.AssertSafe(spans);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_catalog_and_currency_remain_authoritative_across_nested_wrappers(bool harness)
    {
        using var wire = await Wire.Start();
        var chat = new Chat(_ =>
        {
            var response = Response();
            response.ModelId = "gpt-4o-mini";
            response.Usage!.CachedInputTokenCount = 0;
            return response;
        });
        var provider = new CustomOpenAIClient(chat, true, new(false));
        var inner = new LocalCustomAgent(provider);
        inner.Init("timelineFactExtraction", "gpt-4o-mini", Secret, []);
        AgentCostSession? caller = null;
        var tool = AIFunctionFactory.Create(async (string input) =>
        {
            Assert.Same(caller, AgentCostSession.Current);
            await inner.RunAsync(input);
            Assert.Same(caller, AgentCostSession.Current);
            return input;
        }, Secret);
        var outerChat = new Chat(turn =>
        {
            var response = Response(turn == 1);
            response.ModelId = "gpt-4o-mini";
            response.Usage!.CachedInputTokenCount = 0;
            return response;
        });
        var outerProvider = new CustomOpenAIClient(outerChat, true, new(false));
        ICustomAgent outer = harness ? new LocalCustomHarnessAgent(outerProvider) : new LocalCustomAgent(outerProvider);
        outer.Init("timelineFactExtraction", "gpt-4o-mini", Secret, [tool]);
        caller = AgentCostSession.Start(new Dictionary<string, AgentTokenPricing> { ["gpt-4o-mini"] = new(2m, 5m) }, currency: "EUR");
        using (caller)
        {
            await outer.RunAsync(Secret);
            Assert.Same(caller, AgentCostSession.Current);
            Assert.False(caller.GetSnapshot().IsDisposed);
            Assert.Equal(3, caller.GetSnapshot().Totals.CallCount);
            Assert.Equal(0.0009m, caller.GetSnapshot().Totals.EstimatedTotalCost);
            Assert.DoesNotContain(wire.Flush(), span => Name(span) == "agent.session");
        }
        var summary = Assert.Single(wire.Flush(), span => Name(span) == "agent.session");
        Assert.Equal("EUR", Tags(summary)["agent.session.currency"]);
        Assert.Equal(0.0009d, Tags(summary)["agent.session.estimated_total_cost"]);
        Assert.Null(AgentCostSession.Current);
    }

    private static readonly string[] ContentKeys = ["gen_ai.input.messages", "gen_ai.output.messages", "gen_ai.system_instructions",
        "gen_ai.tool.definitions", "gen_ai.agent.id", "gen_ai.response.id", "gen_ai.tool.name", "gen_ai.tool.type",
        "gen_ai.tool.description", "gen_ai.tool.call.id", "gen_ai.tool.call.arguments", "gen_ai.tool.call.result"];

    private static Dictionary<string, AgentTokenPricing> Prices() => new() { [Secret] = new(2m, 5m) };
    public sealed class Result { public string? Value { get; set; } }
    private static StructuredAgentRunOptions Options(Action<ChatResponse>? validate = null) => new()
    {
        ChatOptions = new(), SerializerOptions = new JsonSerializerOptions(),
        ValidateResponse = validate ?? (_ => { }), InvalidResult = () => new InvalidDataException(Secret)
    };

    private static async Task Run(CustomOpenAIClient provider, string kind, bool harness, List<AITool> tools)
    {
        if (kind == "judge")
        {
            var judge = new JudgeAgent(provider);
            judge.Init(Secret, Secret, Secret);
            Assert.Equal(Secret, (await judge.RunAsync(Secret, Secret, [])).Result.Feedback);
        }
        else if (kind == "direct")
        {
            var direct = harness ? provider.CreateHarnessAgent(Secret, Secret, Secret, tools)
                : provider.CreateAgent(Secret, Secret, Secret, tools);
            Assert.Contains(Secret, (await direct.RunAsync(Secret)).Text);
        }
        else
        {
            ICustomAgent agent = harness ? new LocalCustomHarnessAgent(provider) : new LocalCustomAgent(provider);
            agent.Init(Secret, Secret, Secret, tools);
            if (kind == "structured")
                Assert.Equal(Secret, (await agent.RunAsync<Result>(Secret, Options(), default)).Result.Value);
            else if (kind == "typed") Assert.Equal(Secret, (await agent.RunAsync<Result>(Secret)).Result.Value);
            else Assert.Contains(Secret, (await agent.RunAsync(Secret)).Text);
        }
    }

    private static string Name(byte[] span) => Text(span, 5);
    private static byte[] Id(byte[] span) => Messages(span, 2).Single();
    private static byte[] Parent(byte[] span) => Messages(span, 4).SingleOrDefault() ?? [];
    private static Dictionary<string, object> Tags(byte[] span) => Messages(span, 9).ToDictionary(
        tag => Text(tag, 1), tag => AnyValue(Messages(tag, 2).Single()));
    private static string? Operation(Activity activity) => activity.GetTagItem("gen_ai.operation.name") as string;

    private static ChatResponse Response(bool tool = false) => new(new ChatMessage(ChatRole.Assistant,
        tool ? [new FunctionCallContent(Secret, Secret, new Dictionary<string, object?> { ["input"] = Secret })]
            : [new TextContent("{\"Value\":\"" + Secret + "\",\"Criteria\":[],\"Feedback\":\"" + Secret + "\"}")]))
    {
        ModelId = Secret, ResponseId = Secret,
        Usage = new() { InputTokenCount = 100, OutputTokenCount = 20, TotalTokenCount = 120 }
    };

    internal sealed class Chat(Func<int, ChatResponse> respond) : IChatClient
    {
        public int Calls;
        public ConcurrentQueue<Activity?> Activities { get; } = new();
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Activities.Enqueue(Activity.Current);
            var call = Interlocked.Increment(ref Calls);
            await Task.Yield(); // Force real overlap and async context propagation in concurrency cases.
            return respond(call);
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    internal sealed class Wire(IHost host, CaptureHandler transport) : IDisposable
    {
        public IServiceProvider Services => host.Services;
        public CaptureHandler Transport { get; } = transport;
        public static async Task<Wire> Start(bool enabled = true, bool? capture = false)
        {
            var transport = new CaptureHandler();
            var host = new HostBuilder().ConfigureServices(services =>
            {
                services.AddLogfireTelemetry(new ConfigurationBuilder().AddInMemoryCollection(enabled
                    ? new Dictionary<string, string?>
                    {
                        ["Logfire:Endpoint"] = "https://logfire-eu.pydantic.dev/v1/traces",
                        ["Logfire:Token"] = "pylf_v1_dummy_offline_only",
                        ["Logfire:CaptureAgentContent"] = capture?.ToString()
                    } : []).Build());
                services.PostConfigureAll<OtlpExporterOptions>(options => options.HttpClientFactory =
                    () => new HttpClient(transport, disposeHandler: false));
            }).Build();
            await host.StartAsync();
            return new Wire(host, transport);
        }
        public byte[][] Flush()
        {
            var provider = host.Services.GetService<TracerProvider>();
            if (provider is not null) Assert.True(provider.ForceFlush(10_000));
            return Transport.Requests.SelectMany(request => Messages(request.Body, 1))
                .SelectMany(resource => Messages(resource, 2)).SelectMany(scope => Messages(scope, 2)).ToArray();
        }
        public void AssertSafe(byte[][] spans)
        {
            Assert.NotEmpty(Transport.Requests);
            foreach (var request in Transport.Requests)
            {
                Assert.True(request.RawAuthorizationIsCorrect);
                Assert.Equal("application/x-protobuf", request.ContentType);
                Assert.DoesNotContain(Secret, Encoding.UTF8.GetString(request.Body));
            }
            foreach (var span in spans)
            {
                Assert.Contains(Name(span), new[] { "invoke_agent", "chat", "execute_tool", "agent.run", "agent.harness.run", "agent.session" });
                Assert.Empty(Messages(span, 3));
                Assert.Empty(Messages(span, 11));
                Assert.Empty(Messages(span, 13));
                foreach (var status in Messages(span, 15)) Assert.Empty(Messages(status, 2));
                if (Name(span) is "invoke_agent" or "chat" or "execute_tool")
                {
                    var tags = Tags(span);
                    Assert.Equal(Name(span), tags["gen_ai.operation.name"]);
                    Assert.All(tags, tag =>
                    {
                        Assert.Contains(tag.Key, new[] { "gen_ai.operation.name", "gen_ai.agent.name", "gen_ai.request.model",
                            "gen_ai.response.model", "gen_ai.provider.name", "gen_ai.usage.input_tokens", "gen_ai.usage.output_tokens" });
                        if (tag.Key.StartsWith("gen_ai.usage.", StringComparison.Ordinal)) Assert.True(tag.Value is long and >= 0);
                        else if (tag.Key != "gen_ai.operation.name") Assert.Contains(tag.Value, new[] { "other", "openai", "gpt-4o-mini" });
                    });
                }
            }
        }
        public void Dispose() { host.Dispose(); Transport.Dispose(); }
    }

    private sealed class Capture : IDisposable
    {
        private readonly ActivityListener listener;
        public ConcurrentQueue<Activity> Stopped { get; } = new();
        public Capture()
        {
            listener = new()
            {
                ShouldListenTo = source => source.Name == AgentTelemetry.NativeSourceName || source.Name == "Experimental.Microsoft.Agents.AI",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = Stopped.Enqueue
            };
            ActivitySource.AddActivityListener(listener);
        }
        public void Dispose() => listener.Dispose();
    }
}
