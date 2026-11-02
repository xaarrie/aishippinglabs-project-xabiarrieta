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
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(x => x.Name is not ("assessments" or "questions"))
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

    // Deliberately independent of Schema<T>'s extractor/verifier maxItems transform.
    // Query count is bounded by output/time, not an arbitrary array-count ceiling.
    internal static StructuredAgentRunOptions SearchPlanOptions(int outputTokens) => SearchOptions("""
        {"type":"object","additionalProperties":false,"required":["decision","queries","refusalReason"],"properties":{
          "decision":{"type":"string","enum":["Search","Refuse"]},
          "queries":{"type":"array","items":{"type":"string","minLength":1,"maxLength":300}},
          "refusalReason":{"anyOf":[{"type":"string","enum":["OutOfScope","UnsafeRequest"]},{"type":"null"}]}}}
        """, "timeline_search_plan", root =>
    {
        ExactFields(root, "decision", "queries", "refusalReason");
        var decision = root.GetProperty("decision");
        var queries = root.GetProperty("queries");
        var refusal = root.GetProperty("refusalReason");
        if (decision.ValueKind != JsonValueKind.String || queries.ValueKind != JsonValueKind.Array) throw new JsonException();
        if (decision.GetString() == "Search")
        {
            if (refusal.ValueKind != JsonValueKind.Null || queries.GetArrayLength() == 0) throw new JsonException();
            foreach (var query in queries.EnumerateArray())
            {
                if (query.ValueKind != JsonValueKind.String) throw new JsonException();
                TimelineFactInformationSearch.ValidateQuestion(query.GetString()!);
            }
        }
        else if (decision.GetString() != "Refuse" || queries.GetArrayLength() != 0
            || refusal.ValueKind != JsonValueKind.String || refusal.GetString() is not ("OutOfScope" or "UnsafeRequest"))
            throw new JsonException();
    }, outputTokens);

    internal static StructuredAgentRunOptions SearchSynthesisOptions(int outputTokens, TimelineFactCitationCatalog catalog,
        TimelineFactCitationCapture? capture)
    {
        var schema = JsonNode.Parse("""
            {"type":"object","additionalProperties":false,"required":["content","citedFactIds","citedContextEvidenceIds"],"properties":{
              "content":{"type":"string","minLength":1,"maxLength":12000},
              "citedFactIds":{"type":"array","items":{"type":"string","format":"uuid"}},
              "citedContextEvidenceIds":{"type":"array","items":{"type":"string","format":"uuid"}}}}
            """)!;
        void Constrain(string name, IEnumerable<Guid> ids)
        {
            var values = ids.Order().Select(id => JsonValue.Create(id.ToString("D"))).ToArray();
            var array = schema["properties"]![name]!;
            if (values.Length == 0) array["maxItems"] = 0; // enum:[] is not a valid enum schema.
            else array["items"]!["enum"] = new JsonArray(values);
        }
        Constrain("citedFactIds", catalog.Facts);
        Constrain("citedContextEvidenceIds", catalog.Contexts);
        return SearchOptions(schema.ToJsonString(), "timeline_search_synthesis", root =>
        {
            catalog.Validate(root, capture); // Observe BOTH arrays before membership or content rejection.
            ExactFields(root, "content", "citedFactIds", "citedContextEvidenceIds");
            var content = root.GetProperty("content");
            if (content.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(content.GetString())
                || content.GetString()!.Length > 12000) throw new JsonException();
        }, outputTokens);
    }

    private static void ExactFields(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
        var actual = root.EnumerateObject().Select(x => x.Name).ToArray();
        if (actual.Length != names.Length || actual.Distinct().Count() != names.Length
            || !names.All(name => actual.Contains(name))) throw new JsonException();
    }

    private static StructuredAgentRunOptions SearchOptions(string schema, string name, Action<JsonElement> validate, int outputTokens)
    {
        var options = Options(JsonSerializer.Deserialize<JsonElement>(schema), name, validate, "ResponseSchema", "ResponseSchema");
        options.ChatOptions.MaxOutputTokens = outputTokens;
        CustomOpenAIClient.RequireSearchResponseAdmission(options.ChatOptions, response =>
        {
            ValidateEnvelope(response); // Native refusal/truncation keeps its established diagnostic precedence.
            if (response.Messages.Count != 1 || response.Messages[0].Role != ChatRole.Assistant
                || response.Messages[0].Contents.Any(content => content is not TextContent))
                throw new TimelineFactProviderException("InvalidResponse", "ResponseEnvelope");
            options.ValidateResponse(response);
        });
        return options;
    }

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
