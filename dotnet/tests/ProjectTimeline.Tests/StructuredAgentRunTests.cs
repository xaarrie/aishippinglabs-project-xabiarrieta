using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.AgentModels;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.BusinessLogic.Agents.Models;
using Xunit;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace ProjectTimeline.Tests;

public sealed class StructuredAgentRunTests
{
    private static readonly TimelineFactExtraction Source = new("Title", "Source");
    private static readonly TimelineFactCandidate[] Candidates = [new("Claim", "entity", "Thing", "Source")];
    private static string Verification => JsonSerializer.Serialize(new { assessments = new[] { new {
        candidateIndex = 0, supported = true, supportScore = 1.0, issues = Array.Empty<string>(),
        evidenceIds = new[] { TimelineFactSourceEvidence.Create(Source).Last().Id }
    } } });

    private sealed class TypedAgent(string text, CancellationTokenSource? cancel = null) : ICustomAgent
    {
        public event EventHandler<FunctionCallEventArgs>? FunctionCalled { add { } remove { } }
        public int Calls;
        public Type? ResultType;
        public StructuredAgentRunOptions? Options;
        public CancellationToken Token;
        public string? Model;
        public string? Name;
        public string? Input;
        public void Init(string agentName, string model, string instructions, List<AITool> tools)
        {
            Assert.Empty(tools);
            Assert.StartsWith("# ", instructions);
            Model = model;
            Name = agentName;
        }
        public Task<AgentResponse> RunAsync(string message) => throw new Xunit.Sdk.XunitException("Untyped path called");
        public Task<AgentResponse<T>> RunAsync<T>(string message) where T : class => throw new Xunit.Sdk.XunitException("Legacy typed path called");
        public Task<AgentResponse<T>> RunAsync<T>(string message, StructuredAgentRunOptions options, CancellationToken ct) where T : class
        {
            Calls++;
            ResultType = typeof(T);
            Options = options;
            Token = ct;
            Input = message;
            cancel?.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new AgentResponse<T>(new AgentResponse(new ChatMessage(ChatRole.Assistant, text)), options.SerializerOptions));
        }
    }

    // Offline SDK transport also keeps a bypassed typed spy observable without any live calls.
    private sealed class Handler(string text, string finish, string? refusal) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                id = "offline", model = "gpt-4o-mini", created = 0,
                choices = new[] { new { index = 0, finish_reason = finish, message = new { role = "assistant", content = text, refusal } } }
            }), Encoding.UTF8, "application/json") });
    }
    private static CustomOpenAIClient Client(string text, string finish = "stop", string? refusal = null) =>
        new("fake-offline-credential", new HttpClient(new Handler(text, finish, refusal)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Domain_agents_use_typed_run_with_exact_model_contract_and_structured_options(bool verifier)
    {
        var text = verifier ? Verification : "{\"claims\":[]}";
        var spy = new TypedAgent(text);
        if (verifier)
            await new TimelineFactSemanticVerifierAgent(Client(text), "chosen-model", () => spy).VerifyAsync(Source, Candidates, default);
        else
            Assert.Empty(await new TimelineFactExtractionAgent(Client(text), "chosen-model", () => spy).ExtractAsync(Source, default));
        Assert.Equal(1, spy.Calls);
        Assert.Equal(verifier ? typeof(TimelineFactSemanticVerificationResult) : typeof(TimelineFactExtractionResult), spy.ResultType);
        Assert.Equal("chosen-model", spy.Model);
        Assert.Equal(verifier ? "timelineFactSemanticVerifier" : "timelineFactExtraction", spy.Name);
        Assert.True(spy.Token.CanBeCanceled);
        using var input = JsonDocument.Parse(spy.Input!);
        Assert.Equal(Source.Content, (verifier ? input.RootElement.GetProperty("source") : input.RootElement).GetProperty("content").GetString());
        var options = Assert.IsType<StructuredAgentRunOptions>(spy.Options);
        Assert.Equal(12000, options.ChatOptions.MaxOutputTokens);
        Assert.IsType<ChatResponseFormatJson>(options.ChatOptions.ResponseFormat);
        var raw = Assert.IsType<ChatCompletionOptions>(options.ChatOptions.RawRepresentationFactory!(null!));
        using var contract = JsonDocument.Parse(ModelReaderWriterJson(raw.ResponseFormat));
        Assert.True(contract.RootElement.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.Equal(verifier ? "fact_semantic_assessments" : "entry_claims", contract.RootElement.GetProperty("json_schema").GetProperty("name").GetString());
        Assert.Equal(16, options.SerializerOptions.MaxDepth);
        Assert.True(options.SerializerOptions.RespectNullableAnnotations);
        Assert.False(options.SerializerOptions.AllowDuplicateProperties);
        Assert.False(options.SerializerOptions.PropertyNameCaseInsensitive);
    }

    [Theory]
    [InlineData("unknown-property", "CandidateShape")]
    [InlineData("duplicate-property", "CandidateShape")]
    [InlineData("null-string", "CandidateShape")]
    [InlineData("null-item", "CandidateShape")]
    [InlineData("missing-property", "CandidateShape")]
    [InlineData("wrong-case", "CandidateShape")]
    [InlineData("invalid-shape", "CandidateShape")]
    [InlineData("null-root", "ResponseSchema")]
    [InlineData("null-list", "ResponseSchema")]
    [InlineData("unknown-root", "ResponseSchema")]
    [InlineData("duplicate-root", "ResponseSchema")]
    [InlineData("depth", "ResponseSchema")]
    [InlineData("fenced-json", "ResponseSchema")]
    [InlineData("candidate-limit", "CandidateLimit")]
    [InlineData("output-limit", "ResponseLimit")]
    public async Task Extraction_typed_admission_preserves_strict_serializer_limits_and_safe_diagnostics(string mutation, string diagnostic)
    {
        const string claim = "{\"claim\":\"Claim\",\"entityKey\":\"entity\",\"entityType\":\"Thing\",\"sourceQuote\":\"Source\"}";
        var item = mutation switch
        {
            "unknown-property" => claim.Replace("\"claim\":", "\"PRIVATE\":true,\"claim\":"),
            "duplicate-property" => claim.Replace("\"claim\":", "\"claim\":\"PRIVATE\",\"claim\":"),
            "null-string" => claim.Replace("\"Claim\"", "null"),
            "null-item" => "null",
            "missing-property" => claim.Replace("\"claim\":\"Claim\",", ""),
            "wrong-case" => claim.Replace("\"claim\":", "\"Claim\":"),
            "invalid-shape" => claim.Replace("\"entity\"", "\"\""),
            _ => claim
        };
        var text = mutation switch
        {
            "null-root" => "null",
            "null-list" => "{\"claims\":null}",
            "unknown-root" => "{\"claims\":[],\"PRIVATE\":true}",
            "duplicate-root" => "{\"claims\":[],\"claims\":[]}",
            "depth" => "{\"claims\":" + new string('[', 17) + "0" + new string(']', 17) + "}",
            "fenced-json" => "```json\n{\"claims\":[]}\n```",
            "candidate-limit" => "{\"claims\":[" + string.Join(",", Enumerable.Repeat(claim, 200)) + "]}",
            "output-limit" => new string('x', 524289),
            _ => "{\"claims\":[" + item + "]}"
        };
        var error = await Assert.ThrowsAsync<TimelineFactProviderException>(() => new TimelineFactExtractionAgent(Client(text)).ExtractAsync(Source, default));
        Assert.Equal(diagnostic, error.DiagnosticCode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("PRIVATE", error.ToString());
    }

    [Theory]
    [InlineData("length", null, "ResponseTruncated")]
    [InlineData("content_filter", null, "ResponseRefused")]
    [InlineData("stop", "PRIVATE refusal", "ResponseRefused")]
    [InlineData("tool_calls", null, "ResponseEnvelope")]
    public async Task Extraction_metadata_is_rejected_before_invalid_json_is_read(string finish, string? refusal, string diagnostic)
    {
        var error = await Assert.ThrowsAsync<TimelineFactProviderException>(() => new TimelineFactExtractionAgent(
            Client("PRIVATE not JSON", finish, refusal)).ExtractAsync(Source, default));
        Assert.Equal(diagnostic, error.DiagnosticCode);
        Assert.DoesNotContain("PRIVATE", error.ToString());
    }

    private sealed class ChatOnly(ICustomChatClient client) : ICustomChatClient
    {
        public IChatClient GetChatClient(string model) => client.GetChatClient(model);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_chat_only_clients_keep_default_models_and_typed_execution(bool verifier)
    {
        var client = new ChatOnly(Client(verifier ? Verification : "{\"claims\":[]}"));
        if (verifier)
        {
            var agent = new TimelineFactSemanticVerifierAgent(client);
            Assert.Equal("gpt-4o-mini", agent.Model);
            Assert.Single((await agent.VerifyAsync(Source, Candidates, default)).Assessments);
        }
        else
        {
            var agent = new TimelineFactExtractionAgent(client);
            Assert.Equal("gpt-4o-mini", agent.Model);
            Assert.Empty(await agent.ExtractAsync(Source, default));
        }
    }

    private sealed class CancellingChat(CancellationTokenSource cancellation) : IChatClient
    {
        public CancellationToken Token;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            cancellation.Cancel();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Local_adapter_passes_cancellation_to_chat_and_rechecks_before_metadata_admission(bool verifier)
    {
        using var cancellation = new CancellationTokenSource();
        var chat = new CancellingChat(cancellation);
        var client = new CustomOpenAIClient(chat);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (verifier) await new TimelineFactSemanticVerifierAgent(client).VerifyAsync(Source, Candidates, cancellation.Token);
            else await new TimelineFactExtractionAgent(client).ExtractAsync(Source, cancellation.Token);
        });
        Assert.True(chat.Token.IsCancellationRequested);
    }

    private sealed class LegacyAgent : ICustomAgent
    {
        public event EventHandler<FunctionCallEventArgs>? FunctionCalled { add { } remove { } }
        public int Calls;
        public void Init(string agentName, string model, string instructions, List<AITool> tools) { }
        public Task<AgentResponse> RunAsync(string message) => throw new NotSupportedException();
        public Task<AgentResponse<T>> RunAsync<T>(string message) where T : class
        {
            Calls++;
            return Task.FromResult(new AgentResponse<T>(new AgentResponse(new ChatMessage(ChatRole.Assistant, "{\"claims\":[]}")), new(JsonSerializerDefaults.Web) { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() }));
        }
    }

    [Fact]
    public async Task Local_adapter_populates_missing_serializer_metadata_without_mutating_caller_options()
    {
        ICustomAgent agent = new LocalCustomAgent(Client("{\"claims\":[]}"));
        agent.Init("test", "gpt-4o-mini", "test", []);
        var serializer = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var admitted = false;
        var response = await agent.RunAsync<TimelineFactExtractionResult>("input", new StructuredAgentRunOptions
        {
            ChatOptions = new ChatOptions(),
            SerializerOptions = serializer,
            ValidateResponse = response => { Assert.IsType<ChatCompletion>(response.RawRepresentation); admitted = true; },
            InvalidResult = () => new InvalidDataException("Invalid structured result.")
        }, default);
        Assert.True(admitted);
        Assert.Empty(response.Result.Claims);
        Assert.False(serializer.IsReadOnly);
        Assert.Null(serializer.TypeInfoResolver);
    }

    [Fact]
    public async Task Legacy_agent_fakes_keep_their_contract_but_never_silently_downgrade_structured_runs()
    {
        var fake = new LegacyAgent();
        ICustomAgent agent = fake;
        Assert.Empty((await agent.RunAsync<TimelineFactExtractionResult>("input")).Result.Claims);
        await Assert.ThrowsAsync<NotSupportedException>(() => agent.RunAsync<TimelineFactExtractionResult>("input", null!, default));
        Assert.Equal(1, fake.Calls);
    }

    private static string ModelReaderWriterJson(OpenAI.Chat.ChatResponseFormat format) =>
        System.ClientModel.Primitives.ModelReaderWriter.Write(format).ToString();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancellation_reaches_the_typed_run(bool verifier)
    {
        using var cancellation = new CancellationTokenSource();
        var text = verifier ? Verification : "{\"claims\":[]}";
        var spy = new TypedAgent(text, cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (verifier)
                await new TimelineFactSemanticVerifierAgent(Client(text), "gpt-4o-mini", () => spy).VerifyAsync(Source, Candidates, cancellation.Token);
            else
                await new TimelineFactExtractionAgent(Client(text), "gpt-4o-mini", () => spy).ExtractAsync(Source, cancellation.Token);
        });
        Assert.Equal(1, spy.Calls);
        Assert.True(spy.Token.IsCancellationRequested);
    }
}
