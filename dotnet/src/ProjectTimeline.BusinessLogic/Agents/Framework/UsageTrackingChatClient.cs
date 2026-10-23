using Microsoft.Extensions.AI;

namespace ProjectTimeline.BusinessLogic.Agents.Framework;

/// <summary>
/// Install ONCE immediately around the provider client, inside tool/agent aggregation middleware.
/// Observes only task-based ChatResponse usage, not AgentResponse aggregates. Streaming is passed
/// through untracked; callers of streaming APIs need a separate future accounting contract.
/// </summary>
public sealed class UsageTrackingChatClient : DelegatingChatClient
{
    private readonly string? configuredModelId;

    public UsageTrackingChatClient(IChatClient innerClient, string? configuredModelId = null) : base(innerClient)
    {
        if (innerClient is UsageTrackingChatClient)
            throw new ArgumentException("Install usage tracking only once at the provider boundary.", nameof(innerClient));
        this.configuredModelId = configuredModelId;
    }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var session = AgentCostSession.Current; // Capture BEFORE awaiting; never re-read ambient state at completion.
        var model = options?.ModelId ?? configuredModelId;
        if (session is null || !session.BeginCall(model))
            return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

        ChatResponse response;
        try { response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false); }
        catch
        {
            session.CompleteCall(model, null); // Billing may have happened; do not invent zero usage.
            throw;
        }
        session.CompleteCall(model, response);
        return response;
    }
}
