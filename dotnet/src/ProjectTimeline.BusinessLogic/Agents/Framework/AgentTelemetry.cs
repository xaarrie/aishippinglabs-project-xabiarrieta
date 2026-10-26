using System.Diagnostics;

namespace ProjectTimeline.BusinessLogic.Agents.Framework;

/// <summary>Backend-neutral run tracing. The host owns listeners, sampling and exporters.</summary>
public static class AgentTelemetry
{
    /// <summary>Register this source with the host's tracing provider.</summary>
    public const string SourceName = "ProjectTimeline.Agents";

    /// <summary>Native SDK source; hosts must sanitize payloads, including exception events, before export.</summary>
    public const string NativeSourceName = "ProjectTimeline.Agents.Native";

    private static readonly ActivitySource Source = new(SourceName);

    internal static Activity? StartSession(Guid sessionId, string currency) =>
        Source.StartActivity("agent.session", ActivityKind.Internal, parentContext: default(ActivityContext), tags: new ActivityTagsCollection
        {
            { "agent.session.id", sessionId.ToString("D") },
            { "agent.session.currency", currency }
        });

    internal static void CompleteSession(Activity? activity, AgentCostTotals totals)
    {
        if (activity is null) return;
        using (activity)
        {
            activity.SetTag("agent.session.call_count", totals.CallCount);
            activity.SetTag("agent.session.in_flight_call_count", totals.InFlightCallCount);
            activity.SetTag("agent.session.unknown_usage_call_count", totals.UnknownUsageCallCount);
            activity.SetTag("agent.session.unknown_cost_call_count", totals.UnknownCostCallCount);
            activity.SetTag("agent.session.usage_complete", totals.IsUsageComplete);
            activity.SetTag("agent.session.estimated_cost_complete", totals.IsEstimatedCostComplete);
            SetCount("input_tokens", totals.InputTokenCount);
            SetCount("output_tokens", totals.OutputTokenCount);
            SetCount("total_tokens", totals.TotalTokenCount);
            SetCount("cached_input_tokens", totals.CachedInputTokenCount);
            SetCount("reasoning_tokens", totals.ReasoningTokenCount);
            SetCost("known_estimated_cost", totals.KnownEstimatedCost);
            SetCost("estimated_total_cost", totals.EstimatedTotalCost);
        }

        void SetCount(string name, long? value)
        {
            if (value.HasValue) activity.SetTag("agent.session." + name, value.Value);
        }
        void SetCost(string name, decimal? value)
        {
            // OTLP has no decimal scalar. Keep accounting decimal; only trace presentation loses
            // precision through IEEE-754 conversion (never a locale-dependent string).
            if (value.HasValue) activity.SetTag("agent.session." + name, (double)value.Value);
        }
    }

    internal static async Task<T> RunAsync<T>(Func<Task<T>> run, string? agentName, string? model, bool harness, string runKind)
    {
        // Caller/outer scopes own aggregation; only the outermost unscoped wrapper owns disposal.
        // Declared before the wrapper so its summary is published after the awaited run and span stop.
        using var ownedSession = AgentCostSession.Current is null ? AgentCostSession.Start() : null;
        using var activity = Source.StartActivity(harness ? "agent.harness.run" : "agent.run", ActivityKind.Internal,
            parentContext: default(ActivityContext), tags: new ActivityTagsCollection
            {
                { "gen_ai.agent.name", SafeAgentName(agentName) },
                { "gen_ai.request.model", model is null ? "unknown" : model == "gpt-4o-mini" ? model : "other" },
                { "agent.wrapper", harness ? "harness" : "local" },
                { "agent.run.kind", runKind }
            });
        try
        {
            var result = await run();
            activity?.SetTag("agent.run.outcome", "success");
            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch (OperationCanceledException)
        {
            activity?.SetTag("agent.run.outcome", "cancelled");
            throw;
        }
        catch (Exception error)
        {
            activity?.SetTag("agent.run.outcome", "error");
            activity?.SetTag("error.type", error.GetType().FullName);
            // Exception messages, stacks and events may contain provider or user payloads.
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
    }

    // Configuration strings can contain user data or per-run IDs; only known static labels are exported.
    private static string SafeAgentName(string? name) => name switch
    {
        null => "unknown",
        "timelineFactInformation" or "timelineFactOptimization" or "timelineFactExtraction" or
            "timelineFactSemanticVerifier" => name,
        _ => "other"
    };
}
