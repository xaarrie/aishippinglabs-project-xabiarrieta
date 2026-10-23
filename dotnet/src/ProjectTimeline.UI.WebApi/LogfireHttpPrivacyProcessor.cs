using System.Diagnostics;
using OpenTelemetry;

namespace ProjectTimeline.UI.WebApi;

/// <summary>Allowlist only the host HTTP boundary, before the exporter can enqueue it.</summary>
internal sealed class LogfireHttpPrivacyProcessor : BaseProcessor<Activity>
{
    private const string MethodProperty = "ProjectTimeline.SafeHttpMethod";

    internal static void CaptureMethod(Activity activity, HttpRequest request)
    {
        // Strip the untrusted tracestate header after SDK extraction, before agent descendants.
        activity.TraceStateString = null;
        // Capture from the request once, not from mutable activity tags or display names.
        activity.SetCustomProperty(MethodProperty, request.Method switch
        {
            "GET" or "HEAD" or "POST" or "PUT" or "DELETE" or "CONNECT" or "OPTIONS" or "TRACE" or "PATCH" => request.Method,
            _ => "_OTHER"
        });
    }

    private static bool IsHttpServer(Activity activity) => activity.Kind == ActivityKind.Server
        && activity.Source.Name == "Microsoft.AspNetCore"
        && activity.OperationName == "Microsoft.AspNetCore.Hosting.HttpRequestIn";

    public override void OnEnd(Activity activity)
    {
        if (!IsHttpServer(activity)) return;
        var method = activity.GetCustomProperty(MethodProperty) as string ?? "_OTHER";
        var status = activity.GetTagItem("http.response.status_code");
        foreach (var tag in activity.TagObjects.ToArray()) activity.SetTag(tag.Key, null);
        activity.DisplayName = "HTTP request";
        activity.TraceStateString = null;
        activity.SetStatus(activity.Status); // Clear any exception/status description, preserving the code.
        activity.SetTag("http.request.method", method);
        if (status is int code && code is >= 100 and <= 599)
            activity.SetTag("http.response.status_code", code);

        // No supported Activity API removes events/links. Fail closed rather than enqueue
        // exception payloads or link attributes. BatchActivityExportProcessor checks Recorded.
        if (activity.Events.Any() || activity.Links.Any())
            activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
    }
}
