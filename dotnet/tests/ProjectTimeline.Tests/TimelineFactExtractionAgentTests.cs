using Microsoft.Extensions.AI;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class TimelineFactExtractionAgentTests
{
    private sealed class Chat : IChatClient
    {
        public int Calls;
        public ChatOptions? Options;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            Options = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"claims\":[]}")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task Injected_framework_chat_receives_typed_schema_but_missing_raw_metadata_fails_closed()
    {
        var chat = new Chat();
        var framework = new CustomOpenAIClient(chat);
        Assert.Same(chat, framework.GetChatClient("any-model"));
        var agent = new TimelineFactExtractionAgent(framework);
        var error = await Assert.ThrowsAsync<TimelineFactProviderException>(() => agent.ExtractAsync(new("Title", "Source"), default));
        Assert.Equal("ResponseEnvelope", error.DiagnosticCode);
        Assert.Equal(1, chat.Calls);
        Assert.IsType<ChatResponseFormatJson>(chat.Options!.ResponseFormat);
        Assert.Equal(12000, chat.Options.MaxOutputTokens);
    }

    [Fact]
    public async Task Verifier_also_requires_raw_metadata_even_with_a_framework_chat_response()
    {
        var chat = new Chat();
        var error = await Assert.ThrowsAsync<TimelineFactProviderException>(() => new TimelineFactSemanticVerifierAgent(new CustomOpenAIClient(chat))
            .VerifyAsync(new("Title", "Source"), [new("Claim", "entity", "Thing", "Approximate citation")], default));
        Assert.Equal("ResponseEnvelope", error.DiagnosticCode);
        Assert.Equal(1, chat.Calls);
        Assert.IsType<ChatResponseFormatJson>(chat.Options!.ResponseFormat);
    }

    [Fact]
    public async Task Cancelled_and_oversized_source_never_construct_chat()
    {
        var chat = new Chat();
        var agent = new TimelineFactExtractionAgent(new CustomOpenAIClient(chat));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.ExtractAsync(new("Title", "Source"), new CancellationToken(true)));
        await Assert.ThrowsAsync<TimelineFactProviderException>(() => agent.ExtractAsync(new("Title", new string('x', 20001)), default));
        Assert.Equal(0, chat.Calls);
    }
}
