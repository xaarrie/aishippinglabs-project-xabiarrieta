using Microsoft.Extensions.AI;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.BusinessLogic.Agents.Models;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class JudgeAgentTests
{
    // The existing injected-chat seam exercises the real AIAgent without credentials or HTTP.
    private sealed class Chat : IChatClient
    {
        public List<string> Prompts { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Prompts.Add(Assert.Single(messages, message => message.Role == ChatRole.User).Text);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                "{\"Criteria\":[{\"CriterionDescription\":\"Use captured evidence\",\"Passed\":true,\"Judgement\":\"Evidence received\"}],\"Feedback\":\"Offline feedback\"}")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static JudgeAgent CreateJudge(Chat chat)
    {
        var judge = new JudgeAgent(new CustomOpenAIClient(chat));
        judge.Init("offline-judge", "offline-model", "Evaluate only the supplied evidence.");
        return judge;
    }

    private static string ToolEvidence(string prompt) =>
        prompt.Split("<TOOL_CALLS>")[1].Split("</TOOL_CALLS>")[0].Trim();

    [Fact]
    public async Task RunAsync_sends_criteria_output_and_all_calls_with_arguments_to_the_judge()
    {
        var chat = new Chat();
        var judge = CreateJudge(chat);
        const string criteria = "Extract the milestone and consult the source.";
        const string output = "{\"claims\":[\"Milestone delivered\"]}";

        var response = await judge.RunAsync(output, criteria,
        [
            new FunctionCall
            {
                Name = "Search",
                Args = [new() { Name = "query", Value = "Milestone" }, new() { Name = "limit", Value = 2 }]
            },
            new FunctionCall { Name = "Read", Args = [new() { Name = "id", Value = "entry-7" }] }
        ]);

        var prompt = Assert.Single(chat.Prompts);
        Assert.Contains(criteria, prompt);
        Assert.Contains(output, prompt);
        Assert.Equal($"Search(query=Milestone,limit=2){Environment.NewLine}Read(id=entry-7)", ToolEvidence(prompt));
        Assert.Equal("Offline feedback", response.Result.Feedback);
        Assert.True(Assert.Single(response.Result.Criteria).Passed);
    }

    [Theory]
    [InlineData("empty", "Ping()")]
    [InlineData("null-list", "Ping()")]
    [InlineData("null-value", "Ping(optional=null)")]
    public async Task RunAsync_formats_zero_and_null_arguments_without_losing_the_call(string arguments, string expected)
    {
        var chat = new Chat();
        var judge = CreateJudge(chat);
        var call = new FunctionCall { Name = "Ping" };
        if (arguments == "null-list") call.Args = null!;
        if (arguments == "null-value") call.Args = [new() { Name = "optional", Value = null }];

        await judge.RunAsync("output", "criteria", [call]);

        Assert.Equal(expected, ToolEvidence(Assert.Single(chat.Prompts)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_accepts_empty_or_null_call_lists(bool nullList)
    {
        var chat = new Chat();
        var judge = CreateJudge(chat);

        await judge.RunAsync("output", "criteria", nullList ? null! : []);

        Assert.Empty(ToolEvidence(Assert.Single(chat.Prompts)));
    }

    [Fact]
    public async Task RunAsync_does_not_reuse_evidence_from_a_previous_run()
    {
        var chat = new Chat();
        var judge = CreateJudge(chat);
        await judge.RunAsync("previous output", "previous criteria",
            [new FunctionCall { Name = "Read", Args = [new() { Name = "id", Value = "old-entry" }] }]);

        await judge.RunAsync("current output", "current criteria", []);

        Assert.Equal(2, chat.Prompts.Count);
        Assert.Equal("Read(id=old-entry)", ToolEvidence(chat.Prompts[0]));
        Assert.Empty(ToolEvidence(chat.Prompts[1]));
        Assert.Contains("current output", chat.Prompts[1]);
        Assert.Contains("current criteria", chat.Prompts[1]);
        Assert.DoesNotContain("previous output", chat.Prompts[1]);
        Assert.DoesNotContain("previous criteria", chat.Prompts[1]);
    }
}
