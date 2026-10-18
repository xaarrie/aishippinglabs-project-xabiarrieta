using Microsoft.Extensions.AI;
using System.Text.Json;
using ProjectTimeline.BusinessLogic.Agents.AgentModels;

namespace ProjectTimeline.Tests.AgentTests.Support;

// Deterministic wire data ONLY. Not a substitute for any real-model evaluation.
internal sealed class HarnessScriptedTransport(IReadOnlyList<FunctionCallContent[]> batches) : IChatClient
{
    public int Requests { get; private set; }
    public List<ChatOptions> Options { get; } = [];
    public List<IReadOnlyList<ChatMessage>> Messages { get; } = [];
    public static EntryReviewResult WireDraft => new(Status.Ready, [],
        new("Synthetic transport draft", "Not model quality evidence.", [], [], [], []), [], "Transport control only.");

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var index = Requests++;
        Options.Add(options?.Clone() ?? new ChatOptions());
        Messages.Add(messages.ToArray());
        if (index > batches.Count)
            throw new InvalidOperationException("Unexpected follow-up model request in scripted harness control.");
        var message = index < batches.Count
            ? new ChatMessage(ChatRole.Assistant, batches[index].Cast<AIContent>().ToList())
            : new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(WireDraft));
        return Task.FromResult(new ChatResponse(message) { ResponseId = $"wire-{index}" });
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
