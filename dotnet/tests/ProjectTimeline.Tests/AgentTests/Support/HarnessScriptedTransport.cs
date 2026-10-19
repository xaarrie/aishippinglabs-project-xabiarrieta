using Microsoft.Extensions.AI;

namespace ProjectTimeline.Tests.AgentTests.Support;

// Deterministic wire data ONLY. Not a substitute for any real-model evaluation.
internal sealed class HarnessScriptedTransport(IReadOnlyList<FunctionCallContent[]> batches) : IChatClient
{
    public int Requests { get; private set; }
    public List<ChatOptions> Options { get; } = [];
    public List<IReadOnlyList<ChatMessage>> Messages { get; } = [];
    private const string WireResponse = """{"message":"Synthetic transport control only; not model quality evidence."}""";

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
            : new ChatMessage(ChatRole.Assistant, WireResponse);
        return Task.FromResult(new ChatResponse(message) { ResponseId = $"wire-{index}" });
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
