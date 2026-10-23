using System.Diagnostics;
using OpenTelemetry;
using ProjectTimeline.BusinessLogic.Agents.Framework;

namespace ProjectTimeline.UI.WebApi;

/// <summary>Native SDK egress boundary, ordered before batch/exporter enqueue.</summary>
internal sealed class LogfireAgentPrivacyProcessor : BaseProcessor<Activity>
{
    private static bool IsNative(Activity activity) => activity.Source.Name == AgentTelemetry.NativeSourceName;

    public override void OnStart(Activity activity)
    {
        // Clear inherited state before descendants can copy provider/user payloads into their context.
        if (IsNative(activity)) activity.TraceStateString = null;
    }

    public override void OnEnd(Activity activity)
    {
        if (!IsNative(activity)) return;

        var operation = activity.GetTagItem("gen_ai.operation.name") switch
        {
            "invoke_agent" => "invoke_agent",
            "chat" => "chat",
            "execute_tool" => "execute_tool",
            _ => "other"
        };
        var tags = activity.TagObjects.ToArray();
        foreach (var tag in tags) activity.SetTag(tag.Key, null);
        activity.DisplayName = operation;
        activity.TraceStateString = null;
        activity.SetStatus(activity.Status); // Preserve the enum, never the description.
        activity.SetTag("gen_ai.operation.name", operation);

        foreach (var tag in tags)
        {
            switch (tag.Key)
            {
                case "gen_ai.agent.name":
                    activity.SetTag(tag.Key, tag.Value switch
                    {
                        "timelineFactInformation" => "timelineFactInformation",
                        "timelineFactOptimization" => "timelineFactOptimization",
                        "timelineFactExtraction" => "timelineFactExtraction",
                        "timelineFactSemanticVerifier" => "timelineFactSemanticVerifier",
                        _ => "other"
                    });
                    break;
                case "gen_ai.request.model":
                case "gen_ai.response.model":
                    activity.SetTag(tag.Key, tag.Value is "gpt-4o-mini" ? "gpt-4o-mini" : "other");
                    break;
                case "gen_ai.provider.name":
                    activity.SetTag(tag.Key, tag.Value is "openai" ? "openai" : "other");
                    break;
                case "gen_ai.usage.input_tokens":
                case "gen_ai.usage.output_tokens":
                    // SDK integer counts only: no parsing, coercion, arrays or untrusted ToString.
                    if (tag.Value is long and >= 0) activity.SetTag(tag.Key, tag.Value);
                    else if (tag.Value is int count && count >= 0) activity.SetTag(tag.Key, (long)count);
                    break;
            }
        }

        // Activity cannot remove events/links. Even their names/context can contain payloads.
        // Scope metadata is immutable too: only known static SDK versions and no scope tags.
        // Clearing Recorded makes the following batch processor discard this span, not its parent.
        if (activity.Events.Any() || activity.Links.Any()
            || activity.Source.Version is not (null or "" or "1.24.0" or "1.24.0.0")
            || activity.Source.Tags?.Any() == true)
            activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
    }
}
