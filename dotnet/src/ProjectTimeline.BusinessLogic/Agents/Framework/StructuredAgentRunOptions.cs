using Microsoft.Extensions.AI;
using System.Text.Json;

namespace ProjectTimeline.BusinessLogic.Agents.Framework;

/// <summary>Per-run structured contract. Response admission runs before SDK result extraction.</summary>
public sealed class StructuredAgentRunOptions
{
    public required ChatOptions ChatOptions { get; init; }
    public required JsonSerializerOptions SerializerOptions { get; init; }
    public required Action<ChatResponse> ValidateResponse { get; init; }
    public required Func<Exception> InvalidResult { get; init; }
}
