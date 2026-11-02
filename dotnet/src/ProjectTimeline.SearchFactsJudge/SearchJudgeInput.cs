using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectTimeline.EvaluationLabeler;

namespace ProjectTimeline.SearchFactsJudge;

/// <summary>Allowlisted model context. Human annotations and raw scenario/scope metadata have no slot here.</summary>
public sealed class SearchJudgeInput
{
    public string Question { get; }
    public string? Answer { get; }
    public JsonElement Payload { get; }

    private SearchJudgeInput(string question, string? answer, JsonObject payload)
    {
        Question = question;
        Answer = answer;
        Payload = JsonSerializer.SerializeToElement(payload);
    }

    public string ToJson() => Payload.GetRawText();

    public static SearchJudgeInput FromRecord(EvaluationRecord record)
    {
        var response = record.Raw.GetProperty("response");
        var answer = record.Content;
        return new(record.Question, answer, new JsonObject
        {
            ["question"] = record.Question,
            ["answer"] = answer,
            ["responseEvidence"] = Object(response, "", ("facts", v => Array(v, Fact)),
                ("citations", v => Array(v, Citation)), ("sourceContexts", v => Array(v, Context)),
                ("contextCitations", v => Array(v, Context)), ("coverage", Coverage), ("research", Research)),
            ["toolEvidence"] = Tools(record.Raw)
        });
    }

    private static JsonNode? Fact(JsonElement value) => Object(value,
        "id entryId entryNumber claim entityKey entityType sourceQuote sourceKind sourceFingerprint clarificationEvidence creationReviewId extractionJobId");
    private static JsonNode? Citation(JsonElement value) => Object(value,
        "factId entryId entryNumber creationReviewId extractionJobId");
    private static JsonNode? Context(JsonElement value) => Object(value,
        "evidenceId entryId entryNumber sourceFingerprint text truncated evidenceKind",
        ("anchorFactIds", PrimitiveArray));
    private static JsonNode? Coverage(JsonElement value) => Object(value,
        "entries entriesWithoutCompletedExtraction pendingReviews acceptedValidFacts truncated", ("warnings", PrimitiveArray));
    private static JsonNode? Research(JsonElement value) => Object(value,
        "status stopReason modelCalls toolCalls searchQueries evidenceTruncated budgetExhausted", ("warnings", PrimitiveArray),
        ("limits", v => Object(v, "maxModelCalls maxToolCalls maxSearchQueries maxDistinctFacts maxHitsPerSearch maxSourceContexts maxCharsPerContext maxTotalSourceContextChars maxTotalSerializedToolEvidenceChars maxOutputTokens deadlineSeconds")));
    private static JsonNode? ToolPayload(JsonElement value) => Object(value,
        "status query truncated remainingSearchQueries title content text",
        ("facts", v => Array(v, Fact)), ("fact", Fact), ("contexts", v => Array(v, Context)),
        ("results", v => Array(v, ToolPayload)), ("omittedEntryIds", PrimitiveArray));
    private static JsonNode? ToolArguments(JsonElement value) => Object(value, "query entryId",
        ("queries", PrimitiveArray), ("entryIds", PrimitiveArray));

    private static JsonArray Tools(JsonElement root)
    {
        var output = new JsonArray();
        if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array) return output;
        foreach (var message in messages.EnumerateArray())
        {
            if (!message.TryGetProperty("role", out var role) || role.GetString() is not ("assistant" or "tool")
                || !message.TryGetProperty("contents", out var contents) || contents.ValueKind != JsonValueKind.Array) continue;
            foreach (var content in contents.EnumerateArray())
            {
                if (!content.TryGetProperty("kind", out var kind) || kind.GetString() is not ("functionCall" or "functionResult")) continue;
                var projected = Object(content, "kind callId name failed",
                    ("payload", kind.GetString() == "functionCall" ? ToolArguments : ToolPayload));
                output.Add(new JsonObject { ["role"] = role.GetString(), ["content"] = projected });
            }
        }
        return output;
    }

    // Only known structural containers recurse; known scalar fields cannot smuggle metadata objects.
    // Tool serialization uses PascalCase while the saved response uses camelCase; support both.
    private static JsonNode? Object(JsonElement value, string scalarNames,
        params (string Name, Func<JsonElement, JsonNode?> Project)[] children)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        var output = new JsonObject();
        var scalars = scalarNames.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var property in value.EnumerateObject())
        {
            if (scalars.Contains(property.Name, StringComparer.OrdinalIgnoreCase) && IsPrimitive(property.Value))
                output[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            else
                foreach (var child in children)
                    if (string.Equals(child.Name, property.Name, StringComparison.OrdinalIgnoreCase))
                        output[property.Name] = child.Project(property.Value);
        }
        return output;
    }

    private static bool IsPrimitive(JsonElement value) => value.ValueKind is
        JsonValueKind.Null or JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;
    private static JsonNode? PrimitiveArray(JsonElement value) => Array(value,
        item => IsPrimitive(item) ? JsonNode.Parse(item.GetRawText()) : null);
    private static JsonNode? Array(JsonElement value, Func<JsonElement, JsonNode?> project)
    {
        if (value.ValueKind != JsonValueKind.Array) return null;
        var output = new JsonArray();
        foreach (var item in value.EnumerateArray()) output.Add(project(item));
        return output;
    }
}
