using System.Text.Json;
using Microsoft.Extensions.AI;
using ProjectTimeline.BusinessLogic.Agents;
using Xunit;

namespace ProjectTimeline.Tests;

// Scripted responses verify prompt wiring and contracts, not model semantic quality.
[Collection(AgentTelemetryCollection.Name)]
public sealed class SearchEntriesPromptContractTests
{
    [Theory]
    [InlineData("are informational, including descriptions of CRUD or merge behavior")]
    [InlineData("not requests to execute changes")]
    [InlineData("Unknown evidence availability is not OutOfScope")]
    [InlineData("Preserve exact entities and fields")]
    [InlineData("purposeful lexical variants for whole-lexeme retrieval")]
    [InlineData("For actual requests to execute mutations or truly unrelated questions")]
    public async Task Planning_forwards_specification_versus_mutation_and_retrieval_contracts(string contract)
    {
        var (planning, _) = await CaptureInstructionsAsync();
        Assert.Contains(contract, planning);
    }

    [Theory]
    [InlineData("evidence entails the exact entity, field")]
    [InlineData("relationship direction, actor, conditions, scope and modality")]
    [InlineData("Valid citations or a shared topic are insufficient")]
    [InlineData("Do not transfer claims between entities or turn instance state into universal rules")]
    [InlineData("Do not derive workflow or authorization requirements from unrelated presentation facts")]
    [InlineData("Give no plausible but unsupported recommendations")]
    [InlineData("identify unsupported parts; prefer a supported partial answer over invention or blanket refusal")]
    [InlineData("Cite only evidence supporting what you state, never irrelevant evidence as proof")]
    [InlineData("missing or truncated evidence does not prove absence")]
    public async Task Synthesis_forwards_entailment_entity_fidelity_and_partial_answer_contracts(string contract)
    {
        var (_, synthesis) = await CaptureInstructionsAsync();
        Assert.Contains(contract, synthesis);
    }

    private static async Task<(string Planning, string Synthesis)> CaptureInstructionsAsync()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        const string question = "What do the specifications say about delivery ownership and merge permissions?";
        var instructions = new List<string>();
        using var chat = new ScriptChat((turn, messages, options) =>
        {
            Assert.NotNull(options);
            Assert.Empty(options.Tools ?? []);
            Assert.Equal(1500, options.MaxOutputTokens);
            var prompt = Assert.IsType<string>(options.Instructions);
            Assert.Contains("untrusted DATA", prompt);
            instructions.Add(prompt);
            var message = Assert.Single(messages, x => x.Role == ChatRole.User);
            Assert.DoesNotContain(messages, x => x.Role == ChatRole.Assistant || x.Role == ChatRole.Tool);
            var input = JsonSerializer.Deserialize<JsonElement>(message.Text);
            Assert.Equal(question, input.GetProperty("question").GetString());
            if (turn == 1)
            {
                Assert.Contains("# Read-only timeline fact query planning", prompt);
                Assert.DoesNotContain("# Evidence-grounded timeline fact synthesis", prompt);
                Assert.Contains("Return exactly decision, queries, refusalReason", prompt);
                Assert.Contains("1–300 characters", prompt);
                Assert.Contains("There is no query-count ceiling", prompt);
                Assert.Contains("one search batch", prompt);
                Assert.False(input.TryGetProperty("matches", out _));
                Assert.False(input.TryGetProperty("sources", out _));
                return new { decision = "Search", queries = new[] { "alice" }, refusalReason = (string?)null };
            }
            Assert.Equal(2, turn);
            Assert.Contains("# Evidence-grounded timeline fact synthesis", prompt);
            Assert.DoesNotContain("# Read-only timeline fact query planning", prompt);
            Assert.Contains("Return exactly content, citedFactIds, citedContextEvidenceIds", prompt);
            Assert.Contains("Never release an uncited narrative", prompt);
            Assert.Contains("NOT accepted fact proof", prompt);
            Assert.Equal(fact.Id, input.GetProperty("matches").GetProperty("Results")[0]
                .GetProperty("Facts")[0].GetProperty("Id").GetGuid());
            Assert.Single(input.GetProperty("sources").GetProperty("Contexts").EnumerateArray());
            return new
            {
                content = "Alice owns delivery. The supplied evidence does not establish merge permissions.",
                citedFactIds = new[] { fact.Id }, citedContextEvidenceIds = Array.Empty<Guid>()
            };
        });
        var agent = new SearchEntriesAgent(f.Db, new(f.Project.Id, f.Timeline.Id), f.Actor.Id, false, chatClient: chat);
        var result = await agent.RunAsync(question);
        Assert.Equal(2, chat.Calls);
        Assert.Equal("Complete", result.Research!.Status);
        Assert.Equal(fact.Id, Assert.Single(result.Citations).FactId);
        Assert.Equal(2, instructions.Count);
        return (instructions[0], instructions[1]);
    }

    private sealed class ScriptChat(Func<int, IList<ChatMessage>, ChatOptions?, object> script) : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = JsonSerializer.Serialize(script(++Calls, messages.ToList(), options));
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { FinishReason = ChatFinishReason.Stop };
            // Offline provider metadata is required by the production structured-response admission path.
            response.RawRepresentation = System.ClientModel.Primitives.ModelReaderWriter.Read<OpenAI.Chat.ChatCompletion>(
                BinaryData.FromString(JsonSerializer.Serialize(new
                {
                    id = "offline", model = "gpt-4o-mini", created = 0,
                    choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = text } } }
                })))!;
            response.Usage = new() { InputTokenCount = 10, OutputTokenCount = 2, TotalTokenCount = 12 };
            response.ModelId = "gpt-4o-mini";
            return Task.FromResult(response);
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
