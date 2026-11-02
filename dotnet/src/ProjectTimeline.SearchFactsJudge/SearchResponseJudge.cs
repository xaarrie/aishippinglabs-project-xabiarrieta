using Microsoft.Extensions.AI;
using OpenAI.Chat;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using System.Text;
using System.Text.Json;

namespace ProjectTimeline.SearchFactsJudge;

/// <summary>One isolated, tool-free structured call per answer. Injected clients remain caller-owned.</summary>
public sealed class SearchResponseJudge : IDisposable
{
    public const string DefaultModel = "gpt-4o-mini";
    private readonly IChatClient client;
    private readonly string instructions;
    private readonly TimeSpan timeout;
    private bool ownsClient;
    public string Model { get; }

    public SearchResponseJudge(IChatClient chatClient, string? promptPath = null, string model = DefaultModel, TimeSpan? timeout = null)
    {
        client = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("A model is required.", nameof(model));
        Model = model;
        this.timeout = timeout ?? TimeSpan.FromSeconds(60);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Use a finite timeout of at most ten minutes.");
        instructions = File.ReadAllText(promptPath ?? Path.Combine(AppContext.BaseDirectory, "Prompts", "SearchResponseJudge.md"));
        if (string.IsNullOrWhiteSpace(instructions)) throw new ArgumentException("The judge prompt must not be blank.", nameof(promptPath));
    }

    // Explicit credential injection: the core never reads environment variables or credential files.
    // This reuses the repo's bounded SDK transport and zero-retry provider configuration.
    public static SearchResponseJudge CreateOpenAI(string apiKey, string? promptPath = null, string model = DefaultModel,
        TimeSpan? timeout = null, HttpClient? transport = null)
    {
        var provider = new CustomOpenAIClient(apiKey, transport, isolateHarnessFacilities: true,
            telemetryOptions: new(CaptureAgentContent: false));
        var chat = provider.GetChatClient(model);
        try { return new(chat, promptPath, model, timeout) { ownsClient = true }; }
        catch { chat.Dispose(); throw; }
    }

    public async Task<SearchResponseJudgment> JudgeAsync(SearchJudgeInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ct.ThrowIfCancellationRequested();
        if (input.Answer is null) throw new SearchJudgeException("NoAnswer");
        var message = input.ToJson();
        if (Encoding.UTF8.GetByteCount(message) > 768 * 1024) throw new SearchJudgeException("InputLimit");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lifetime.CancelAfter(timeout);
        // No mutable conversation/session is retained between rows, including after errors.
        var provider = new CustomOpenAIClient(new AdmissionClient(client), isolateHarnessFacilities: true,
            telemetryOptions: new(CaptureAgentContent: false));
        var agent = new LocalCustomAgent(provider);
        try
        {
            agent.Init("searchResponseJudge", Model, instructions, []);
            var response = await agent.RunAsync<SearchResponseJudgment>(message, Options(), lifetime.Token).WaitAsync(lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            return response.Result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { throw new OperationCanceledException("Judge call cancelled.", ct); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        { throw new SearchJudgeException("Timeout"); }
        catch (SearchJudgeException) { throw; }
        catch (Exception) { throw new SearchJudgeException("ProviderFailure"); }
    }

    private static StructuredAgentRunOptions Options() => new()
    {
        ChatOptions = new ChatOptions
        {
            MaxOutputTokens = 700,
            Temperature = 0,
            ResponseFormat = Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema(SearchResponseJudgment.Schema, "search_response_judgment"),
            RawRepresentationFactory = _ => new ChatCompletionOptions
            {
                ResponseFormat = OpenAI.Chat.ChatResponseFormat.CreateJsonSchemaFormat("search_response_judgment",
                    BinaryData.FromString(SearchResponseJudgment.Schema.GetRawText()), jsonSchemaIsStrict: true)
            }
        },
        SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            MaxDepth = 4, RespectNullableAnnotations = true, AllowDuplicateProperties = false,
            PropertyNameCaseInsensitive = false, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
        },
        ValidateResponse = Validate,
        InvalidResult = () => new SearchJudgeException("ResponseSchema")
    };

    private static void Validate(ChatResponse response)
    {
        if (response.RawRepresentation is ChatCompletion raw)
        {
            if (raw.Refusal is not null || raw.FinishReason == OpenAI.Chat.ChatFinishReason.ContentFilter)
                throw new SearchJudgeException("ResponseRefused");
            if (raw.FinishReason == OpenAI.Chat.ChatFinishReason.Length) throw new SearchJudgeException("ResponseTruncated");
            if (raw.FinishReason != OpenAI.Chat.ChatFinishReason.Stop) throw new SearchJudgeException("ResponseEnvelope");
        }
        if (response.FinishReason == Microsoft.Extensions.AI.ChatFinishReason.ContentFilter) throw new SearchJudgeException("ResponseRefused");
        if (response.FinishReason == Microsoft.Extensions.AI.ChatFinishReason.Length) throw new SearchJudgeException("ResponseTruncated");
        if (response.FinishReason != Microsoft.Extensions.AI.ChatFinishReason.Stop || response.Messages.Count != 1
            || response.Messages[0].Role != ChatRole.Assistant || response.Messages[0].Contents.Any(c => c is not TextContent))
            throw new SearchJudgeException("ResponseEnvelope");
        _ = SearchResponseJudgment.Parse(response.Text);
    }

    // Admission below the SDK's function-invocation layer prevents unexpected calls/continuations.
    // Sanitize provider exceptions here, BEFORE native tracing can record their payloads.
    private sealed class AdmissionClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await base.GetResponseAsync(messages, options, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                Validate(response);
                return response;
            }
            catch (SearchJudgeException) { throw; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { throw new OperationCanceledException("Judge call cancelled.", cancellationToken); }
            catch (Exception) { throw new SearchJudgeException("ProviderFailure"); }
        }
    }

    public void Dispose()
    {
        if (ownsClient) { ownsClient = false; client.Dispose(); }
    }
}
