using System.Diagnostics;
using OpenTelemetry;
using ProjectTimeline.BusinessLogic.Agents.Framework;

namespace ProjectTimeline.UI.WebApi;

/// <summary>Native SDK egress boundary, ordered before batch/exporter enqueue.</summary>
internal sealed class LogfireAgentPrivacyProcessor(AgentTelemetryOptions options) : BaseProcessor<Activity>
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
            // Installed SDK 1.24.0 / Extensions.AI 10.10.0 emit these as string tags,
            // not events. Admit their full native values only under the shared policy.
            // Never broaden this to gen_ai.*: SDK errors also carry exception payloads.
            if (options.CaptureAgentContent && tag.Value is string && tag.Key is
                ("gen_ai.input.messages" or "gen_ai.output.messages" or "gen_ai.system_instructions"
                or "gen_ai.tool.definitions" or "gen_ai.agent.id" or "gen_ai.response.id"
                or "gen_ai.tool.name" or "gen_ai.tool.type" or "gen_ai.tool.description"
                or "gen_ai.tool.call.id" or "gen_ai.tool.call.arguments" or "gen_ai.tool.call.result"
                or "gen_ai.agent.name" or "gen_ai.request.model" or "gen_ai.response.model" or "gen_ai.provider.name"))
            {
                activity.SetTag(tag.Key, tag.Value);
                continue;
            }
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
