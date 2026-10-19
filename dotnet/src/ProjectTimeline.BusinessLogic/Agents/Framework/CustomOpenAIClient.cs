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
        private readonly OpenAIClient? sdkClient;
        private static readonly HttpClient BoundedTransport = new(new BoundedResponseHandler(null));

        // Caller owns an injected transport; SDK retries are explicitly disabled for bounded billing.
        public CustomOpenAIClient(string apiKey, HttpClient? transport = null, bool isolateHarnessFacilities = false)
        {
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
                var response = await sdk.GetEmbeddingClient("text-embedding-3-small").GenerateEmbeddingsAsync(
                    new[] { input }, new OpenAI.Embeddings.EmbeddingGenerationOptions { Dimensions = 1536 }, token);
                if (response.Value.Model != "text-embedding-3-small" || response.Value.Count != 1 || response.Value[0].Index != 0)
                    throw new TimelineFactProviderException("InvalidResponse");
                var vector = response.Value[0].ToFloats().ToArray();
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
        public CustomOpenAIClient(bool isolateHarnessFacilities)
        {
            this.isolateHarnessFacilities = isolateHarnessFacilities;
            openaikey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
            if (string.IsNullOrWhiteSpace(openaikey))
                throw new InvalidOperationException("OPENAI_API_KEY environment variable is required.");
        }

        // Infrastructure seam: all factories use this transport; no environment credentials needed.
        public CustomOpenAIClient(IChatClient chatClient, bool isolateHarnessFacilities = false)
        {
            injectedClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
            this.isolateHarnessFacilities = isolateHarnessFacilities;
        }

        public AIAgent CreateAgent(string model, string agentName, string instructions, List<AITool> tools)
        {
            
            var client = GetChatClient(model);
            var agent = client.AsAIAgent(
            name: agentName,
            instructions: instructions,
            tools: tools);
            return agent;
        }

        public AIAgent CreateHarnessAgent(string model, string agentName, string instructions, List<AITool> tools)
        {
            return GetChatClient(model).AsHarnessAgent(
                new HarnessAgentOptions
                {
                    Name = agentName,
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
