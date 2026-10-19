using Microsoft.Extensions.AI;
using OpenAI.Chat;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace ProjectTimeline.BusinessLogic.Agents;

// Shared bounded single-response transport, not an agent loop. Callers own their typed contracts.
internal static class TimelineFactStructuredResponse
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { MaxDepth = 16, RespectNullableAnnotations = true, AllowDuplicateProperties = false, PropertyNameCaseInsensitive = false };

    // Schema inference may set TypeInfoResolver; never share the serializer instance it can freeze/mutate.
    internal static JsonElement Schema<T>() => AIJsonUtilities.CreateJsonSchema(typeof(T), serializerOptions: new JsonSerializerOptions(Json),
        inferenceOptions: new AIJsonSchemaCreateOptions
        {
            TransformSchemaNode = (_, node) =>
            {
                if (node is JsonObject obj && obj["type"]?.ToString() == "array") obj["maxItems"] = 200;
                return node;
            }
        });

    internal static void ValidateSource(TimelineFactExtraction source)
    {
        if (source is null || source.Title is null || source.Content is null || source.Title.Length > 200 || source.Content.Length > 20000)
            throw new TimelineFactProviderException("InvalidResponse", "InputLimit");
    }

    internal static async Task<string> ReadAsync(ICustomChatClient client, string model, string instructions,
        object input, JsonElement schema, string schemaName, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(input, Json);
        if (Encoding.UTF8.GetByteCount(payload) > 768 * 1024)
            throw new TimelineFactProviderException("InvalidResponse", "InputLimit");
        var response = await client.GetChatClient(model).GetResponseAsync([
            new ChatMessage(ChatRole.System, instructions), new ChatMessage(ChatRole.User, payload)
        ], new ChatOptions
        {
            MaxOutputTokens = 12000,
            ResponseFormat = Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema(schema, schemaName),
            RawRepresentationFactory = _ => new ChatCompletionOptions
            {
                ResponseFormat = OpenAI.Chat.ChatResponseFormat.CreateJsonSchemaFormat(schemaName,
                    BinaryData.FromString(schema.GetRawText()), jsonSchemaIsStrict: true)
            }
        }, ct);
        ct.ThrowIfCancellationRequested();
        if (response.RawRepresentation is not ChatCompletion raw)
            throw new TimelineFactProviderException("InvalidResponse", "ResponseEnvelope");
        if (raw.FinishReason == OpenAI.Chat.ChatFinishReason.Length)
            throw new TimelineFactProviderException("InvalidResponse", "ResponseTruncated");
        if (raw.Refusal is not null || raw.FinishReason == OpenAI.Chat.ChatFinishReason.ContentFilter)
            throw new TimelineFactProviderException("InvalidResponse", "ResponseRefused");
        if (raw.FinishReason != OpenAI.Chat.ChatFinishReason.Stop || response.Messages.Count != 1)
            throw new TimelineFactProviderException("InvalidResponse", "ResponseEnvelope");
        var text = response.Text;
        if (Encoding.UTF8.GetByteCount(text) > 512 * 1024)
            throw new TimelineFactProviderException("InvalidResponse", "ResponseLimit");
        return text;
    }
}
