using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProjectTimeline.SearchFactsJudge;

// Codes, never provider payloads or exception messages, are safe for runner error exports.
public sealed class SearchJudgeException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record SearchResponseJudgment(
    [property: JsonPropertyName("reasoning")] string Reasoning,
    [property: JsonPropertyName("label")] string Label)
{
    public static JsonElement Schema { get; } = JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","additionalProperties":false,"required":["reasoning","label"],"properties":{
          "reasoning":{"type":"string","minLength":1,"maxLength":2000,"description":"Brief rationale grounded in the captured answer and evidence; not a chain of thought."},
          "label":{"type":"string","enum":["good","bad"]}}}
        """);

    public static SearchResponseJudgment Parse(string json)
    {
        if (json is null || Encoding.UTF8.GetByteCount(json) > 16 * 1024)
            throw new SearchJudgeException("ResponseLimit");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
                || !root.TryGetProperty("reasoning", out var reasoning) || reasoning.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("label", out var label) || label.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(reasoning.GetString()) || reasoning.GetString()!.Length > 2000
                || label.GetString() is not ("good" or "bad"))
                throw new SearchJudgeException("ResponseSchema");
            return new(reasoning.GetString()!, label.GetString()!);
        }
        catch (JsonException) { throw new SearchJudgeException("ResponseSchema"); }
    }
}
