using Microsoft.Extensions.AI;
using OpenAI.Chat;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProjectTimeline.BusinessLogic.Agents;

// Shared bounded structured-run contracts. Domain agents consume only admitted typed results.
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

    internal static ICustomAgent CreateAgent(ICustomChatClient client, string model) =>
        new LocalCustomAgent(client as ICustomAgentClient ?? new CustomOpenAIClient(client.GetChatClient(model)));

    internal static string SerializeInput(object input)
    {
        var payload = JsonSerializer.Serialize(input, Json);
        if (Encoding.UTF8.GetByteCount(payload) > 768 * 1024)
            throw new TimelineFactProviderException("InvalidResponse", "InputLimit");
        return payload;
    }

    internal static StructuredAgentRunOptions ExtractionOptions(JsonElement schema) => Options(schema, "entry_claims", root =>
    {
        // Keep root errors distinct from malformed candidate diagnostics.
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1
            || !root.TryGetProperty("claims", out var claims) || claims.ValueKind != JsonValueKind.Array)
            throw new JsonException();
        if (claims.GetArrayLength() >= 200) throw new TimelineFactProviderException("InvalidResponse", "CandidateLimit");
    }, "ResponseSchema", "CandidateShape");

    internal static StructuredAgentRunOptions VerificationOptions(JsonElement schema, int count) => Options(schema, "fact_semantic_assessments", root =>
    {
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1
            || !root.TryGetProperty("assessments", out var list) || list.ValueKind != JsonValueKind.Array
            || list.GetArrayLength() != count) throw new JsonException();
        // JSON enum converters accept integers and case variants by default; this contract does not.
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("issues", out var issues)
                || issues.ValueKind != JsonValueKind.Array || issues.EnumerateArray().Any(issue =>
                    issue.ValueKind != JsonValueKind.String || issue.GetString() is not ("Numeric" or "Date" or "Actor" or "Negation" or "Unsupported")))
                throw new JsonException();
        }
    }, "MalformedAssessment", "MalformedAssessment");

    private static StructuredAgentRunOptions Options(JsonElement schema, string schemaName, Action<JsonElement> validateRoot,
        string rootDiagnostic, string resultDiagnostic) => new()
    {
        ChatOptions = new ChatOptions
        {
            MaxOutputTokens = 12000,
            ResponseFormat = Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema(schema, schemaName),
            RawRepresentationFactory = _ => new ChatCompletionOptions
            {
                ResponseFormat = OpenAI.Chat.ChatResponseFormat.CreateJsonSchemaFormat(schemaName,
                    BinaryData.FromString(schema.GetRawText()), jsonSchemaIsStrict: true)
            }
        },
        SerializerOptions = new JsonSerializerOptions(Json),
        ValidateResponse = response =>
        {
            ValidateEnvelope(response);
            try
            {
                using var document = JsonDocument.Parse(response.Text, new JsonDocumentOptions { MaxDepth = 16 });
                validateRoot(document.RootElement);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
            { throw new TimelineFactProviderException("InvalidResponse", rootDiagnostic); }
        },
        InvalidResult = () => new TimelineFactProviderException("InvalidResponse", resultDiagnostic)
    };

    private static void ValidateEnvelope(ChatResponse response)
    {
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
    }
}
