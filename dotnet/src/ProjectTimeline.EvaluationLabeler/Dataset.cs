using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProjectTimeline.EvaluationLabeler;

public sealed class LabelerException(string code, int line = 0, int status = 400) : Exception(code)
{
    public string Code { get; } = code;
    public int Line { get; } = line;
    public int Status { get; } = status;
}

public sealed record EvaluationRecord(int Index, string RunId, string ScenarioId, int Ordinal,
    string Question, JsonElement Raw)
{
    public string? Content => Raw.GetProperty("response").ValueKind == JsonValueKind.Null
        ? null : Raw.GetProperty("response").GetProperty("content").GetString();
    public string ScenarioType
    {
        get
        {
            var scenario = Raw.GetProperty("scenario");
            if (!scenario.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
                return "Not available";
            foreach (var item in fields.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("header", out var header) || header.ValueKind != JsonValueKind.String
                    || !string.Equals(header.GetString(), "type", StringComparison.OrdinalIgnoreCase))
                    continue;
                return item.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : "Not available";
            }
            return "Not available";
        }
    }
    public string Outcome => Raw.GetProperty("outcome").GetString()!;
    public string ResearchStatus => Raw.GetProperty("response").ValueKind == JsonValueKind.Object
        && Raw.GetProperty("response").TryGetProperty("status", out var status) ? status.ToString() : "Not available";
    public string Detail(string name) => Raw.TryGetProperty(name, out var value) ? value.GetRawText() : "Not saved";
}

public sealed record Dataset(string Path, string Sha256, ImmutableArray<EvaluationRecord> Records)
{
    public static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static Dataset Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw new LabelerException("input-utf8"); }
        if (text.StartsWith('\uFEFF')) text = text[1..];
        var records = ImmutableArray.CreateBuilder<EvaluationRecord>();
        var identities = new HashSet<(string, string)>();
        using var reader = new StringReader(text);
        var line = 0;
        while (reader.ReadLine() is { } row)
        {
            line++;
            try
            {
                using var doc = JsonDocument.Parse(row);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new LabelerException("input-object", line);
                CheckUniqueProperties(root, line);
                var run = RequiredString(root, "runId", line);
                var id = RequiredString(root, "scenarioId", line);
                _ = RequiredString(root, "outcome", line);
                if (!root.TryGetProperty("scenario", out var scenario) || scenario.ValueKind != JsonValueKind.Object)
                    throw new LabelerException("input-scenario", line);
                var question = RequiredString(scenario, "question", line);
                if (!scenario.TryGetProperty("ordinal", out var ordinal) || ordinal.ValueKind != JsonValueKind.Number
                    || !ordinal.TryGetInt32(out var number) || number < 1)
                    throw new LabelerException("input-ordinal", line);
                if (!root.TryGetProperty("response", out var response)
                    || response.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                    throw new LabelerException("input-response", line);
                if (response.ValueKind == JsonValueKind.Object
                    && (!response.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String))
                    throw new LabelerException("input-content", line);
                if (!identities.Add((run, id))) throw new LabelerException("input-duplicate-identity", line);
                records.Add(new(records.Count, run, id, number, question, root.Clone()));
            }
            catch (JsonException) { throw new LabelerException("input-json", line); }
        }
        if (records.Count == 0) throw new LabelerException("input-empty");
        return new(System.IO.Path.GetFullPath(path), Convert.ToHexStringLower(SHA256.HashData(bytes)), records.ToImmutable());
    }

    private static string RequiredString(JsonElement value, string name, int line)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString())) throw new LabelerException("input-required-" + name, line);
        return property.GetString()!;
    }

    private static void CheckUniqueProperties(JsonElement value, int line)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new LabelerException("input-duplicate-property", line);
                CheckUniqueProperties(property.Value, line);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in value.EnumerateArray()) CheckUniqueProperties(child, line);
        }
    }
}
