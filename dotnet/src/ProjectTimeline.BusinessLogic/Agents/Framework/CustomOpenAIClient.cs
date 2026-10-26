using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System;
using System.Collections.Generic;
using System.Text;

namespace ProjectTimeline.BusinessLogic.Agents.Framework
{
    public class CustomOpenAIClient : ICustomChatClient, ICustomAgentClient, ICustomEmbeddingClient
    {
        private readonly string? openaikey;
        private readonly IChatClient? injectedClient;
        private readonly bool isolateHarnessFacilities;
        private readonly AgentTelemetryOptions telemetryOptions;
        private readonly OpenAIClient? sdkClient;
        private static readonly HttpClient BoundedTransport = new(new BoundedResponseHandler(null));

        // Caller owns an injected transport; SDK retries are explicitly disabled for bounded billing.
        public CustomOpenAIClient(string apiKey, HttpClient? transport = null, bool isolateHarnessFacilities = false)
            : this(apiKey, transport, isolateHarnessFacilities, new()) { }

        public CustomOpenAIClient(string apiKey, HttpClient? transport, bool isolateHarnessFacilities,
            AgentTelemetryOptions? telemetryOptions)
        {
            this.telemetryOptions = telemetryOptions ?? new();
            if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("Provider credential required.");
            this.isolateHarnessFacilities = isolateHarnessFacilities;
            var bounded = transport is null ? BoundedTransport : new HttpClient(new BoundedResponseHandler(transport));
            sdkClient = new OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey), new OpenAIClientOptions
            {
                Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(bounded),
                RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(0),
                NetworkTimeout = TimeSpan.FromSeconds(90)
            });
        }

        public Task<float[]> EmbedAsync(string input, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(input) || input.Length > 4000)
                throw new TimelineFactProviderException("InvalidResponse");
            return TimelineFactProviderCall.RunAsync(async token =>
            {
                var sdk = sdkClient ?? (openaikey is not null ? new CustomOpenAIClient(openaikey).sdkClient : null);
                if (sdk is null) throw new TimelineFactProviderException("Unavailable");
                const string model = "text-embedding-3-small";
                using var ownedSession = AgentCostSession.Current is null ? AgentCostSession.Start() : null;
                var session = AgentCostSession.Current;
                var admitted = session?.BeginCall(model) == true;
                OpenAI.Embeddings.OpenAIEmbeddingCollection? embeddings = null;
                try
                {
                    var result = await sdk.GetEmbeddingClient(model).GenerateEmbeddingsAsync(
                        new[] { input }, new OpenAI.Embeddings.EmbeddingGenerationOptions { Dimensions = 1536 }, token);
                    embeddings = result.Value;
                }
                finally
                {
                    // Actual provider usage may be billed even when subsequent vector validation fails.
                    if (admitted) session!.CompleteEmbeddingCall(model, embeddings);
                }
                var response = embeddings!;
                if (response.Model != model || response.Count != 1 || response[0].Index != 0)
                    throw new TimelineFactProviderException("InvalidResponse");
                var vector = response[0].ToFloats().ToArray();
                TimelineFactProcessing.ValidateVector(vector);
                return vector;
            }, ct);
        }

        private sealed class BoundedResponseHandler(HttpClient? injected) : HttpMessageHandler
        {
            private readonly HttpClient transport = injected ?? new HttpClient();
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri) { Content = request.Content };
                foreach (var header in request.Headers) forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
                var response = await transport.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, ct);
                try
                {
                    const int limit = 512 * 1024;
                    if (response.Content.Headers.ContentLength > limit) throw new TimelineFactProviderException("InvalidResponse", "ResponseLimit");
                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    using var buffer = new MemoryStream();
                    var chunk = new byte[8192];
                    int read;
                    while ((read = await stream.ReadAsync(chunk, ct)) > 0)
                    {
                        if (buffer.Length + read > limit) throw new TimelineFactProviderException("InvalidResponse", "ResponseLimit");
                        buffer.Write(chunk, 0, read);
                    }
                    if (response.IsSuccessStatusCode && request.RequestUri!.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal))
                    {
                        try
                        {
                            using var document = System.Text.Json.JsonDocument.Parse(buffer.ToArray(), new System.Text.Json.JsonDocumentOptions { MaxDepth = 32 });
                            var choices = document.RootElement.GetProperty("choices");
                            if (choices.GetArrayLength() != 1 || !choices[0].TryGetProperty("finish_reason", out var finish)
                                || finish.ValueKind != System.Text.Json.JsonValueKind.String
                                || finish.GetString() is not ("stop" or "length" or "content_filter" or "tool_calls" or "function_call"))
                                throw new TimelineFactProviderException("InvalidResponse", "ResponseEnvelope");
                        }
                        catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
                        { throw new TimelineFactProviderException("InvalidResponse", "ResponseEnvelope"); }
                    }
                    var replacement = new ByteArrayContent(buffer.ToArray());
                    foreach (var header in response.Content.Headers) replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    response.Content.Dispose();
                    response.Content = replacement;
                    return response;
                }
                catch { response.Dispose(); throw; }
            }
        }

        public CustomOpenAIClient() : this(isolateHarnessFacilities: false) { }

        // Opt-in only: existing callers retain the SDK's local facilities and key validation.
        public CustomOpenAIClient(bool isolateHarnessFacilities) : this(isolateHarnessFacilities, new()) { }

        public CustomOpenAIClient(bool isolateHarnessFacilities, AgentTelemetryOptions? telemetryOptions)
        {
            this.telemetryOptions = telemetryOptions ?? new();
            this.isolateHarnessFacilities = isolateHarnessFacilities;
            openaikey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
            if (string.IsNullOrWhiteSpace(openaikey))
                throw new InvalidOperationException("OPENAI_API_KEY environment variable is required.");
        }

        // Infrastructure seam: all factories use this transport; no environment credentials needed.
        public CustomOpenAIClient(IChatClient chatClient, bool isolateHarnessFacilities = false)
            : this(chatClient, isolateHarnessFacilities, new()) { }

        public CustomOpenAIClient(IChatClient chatClient, bool isolateHarnessFacilities,
            AgentTelemetryOptions? telemetryOptions)
        {
            this.telemetryOptions = telemetryOptions ?? new();
            injectedClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
            this.isolateHarnessFacilities = isolateHarnessFacilities;
        }

        // Content capture defaults on. Hosts still need an egress privacy boundary: even
        // sensitive=false does not suppress SDK exception text. Web API sanitizes this source.
        public AIAgent CreateAgent(string model, string agentName, string instructions, List<AITool> tools)
        {
            
            var client = GetTrackedChatClient(model);
            var agent = client.AsAIAgent(
            name: agentName,
            instructions: instructions,
            tools: tools);
            return WithNativeTelemetry(agent);
        }

        public AIAgent CreateHarnessAgent(string model, string agentName, string instructions, List<AITool> tools)
        {
            // Harness supplies its own chat pipeline with no deferred auto-wiring slot.
            // Instrument the leaf explicitly, below its function-invocation loop.
            var client = new OpenTelemetryChatClient(GetTrackedChatClient(model), sourceName: AgentTelemetry.NativeSourceName)
            {
                EnableSensitiveData = telemetryOptions.CaptureAgentContent
            };
            var agent = client.AsHarnessAgent(
                new HarnessAgentOptions
                {
                    Name = agentName,
                    // Replace the default-source wrapper, rather than nesting a second agent span.
                    DisableOpenTelemetry = true,
                    HarnessInstructions = "Use tools deliberately and report verified results.",
                    ChatOptions = new ChatOptions
                    {
                        Instructions = instructions,
                        Tools = tools,
                       
                    },
                    DisableWebSearch = true,
                    DisableFileMemory = isolateHarnessFacilities,
                    DisableAgentSkillsProvider = isolateHarnessFacilities,
                    DisableTodoProvider = isolateHarnessFacilities,
                    DisableAgentModeProvider = isolateHarnessFacilities,
                    // File access and background agents remain unconfigured (SDK opt-in).
                  
                }
                );
            return WithNativeTelemetry(agent);
        }

        private AIAgent WithNativeTelemetry(AIAgent agent) => agent.AsBuilder()
            // Plain agents expose the SDK slot below tool aggregation. Harness has no slot
            // and is explicitly instrumented above; auto-wiring does not add a second client.
            .UseOpenTelemetry(AgentTelemetry.NativeSourceName, configure: telemetry => telemetry.EnableSensitiveData = telemetryOptions.CaptureAgentContent)
            .Build();

        // Install before SDK tool aggregation, even when no session exists yet. Structured run
        // middleware wraps this same pipeline; it must not install a second accounting decorator.
        private IChatClient GetTrackedChatClient(string model)
        {
            var client = GetChatClient(model);
            var tracked = client is UsageTrackingChatClient ? client : new UsageTrackingChatClient(client, model);
            // Call-time ambient admission stays above accounting, even for already initialized agents.
            return new ExecutionBudgetChatClient(tracked);
        }

        public IChatClient GetChatClient(string model)
        {
            if (injectedClient is not null)
                return injectedClient;
            var client = sdkClient ?? new OpenAIClient(openaikey!);
            return client.GetChatClient(model).AsIChatClient();
        }
    }
}
