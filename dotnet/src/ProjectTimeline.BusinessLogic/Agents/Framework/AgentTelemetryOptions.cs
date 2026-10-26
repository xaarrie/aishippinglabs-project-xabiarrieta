namespace ProjectTimeline.BusinessLogic.Agents.Framework;

/// <summary>Immutable capture policy. Content is disclosed by default; pass false for private traces.</summary>
public sealed record AgentTelemetryOptions(bool CaptureAgentContent = true);
