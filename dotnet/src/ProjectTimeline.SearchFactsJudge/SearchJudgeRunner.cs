using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProjectTimeline.EvaluationLabeler;

namespace ProjectTimeline.SearchFactsJudge;

public sealed record SearchJudgeRunResult(string Status, SearchJudgeMetrics Metrics)
{
    public int ExitCode => Status == "Completed" ? 0 : 1;
}

/// <summary>Single-pass calibration over saved answers. The delegate is the deterministic test seam.</summary>
public static class SearchJudgeRunner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions Pretty = new(Json) { WriteIndented = true };
    private sealed record Provenance(int SchemaVersion, string EvaluationKind, string DatasetSha256,
        string LabelsSha256, string PromptSha256, string Model, int TimeoutSeconds,
        DateTimeOffset StartedUtc, DateTimeOffset? FinishedUtc, string Status);

    public static async Task<SearchJudgeRunResult> RunAsync(SearchJudgeCommand command,
        Func<SearchJudgeInput, string, CancellationToken, Task<SearchResponseJudgment>> judge,
        TextWriter progress, CancellationToken ct = default)
    {
        // All validation and immutable input snapshots precede output creation and paid calls.
        if (File.Exists(command.Output) || Directory.Exists(command.Output) || new DirectoryInfo(command.Output).LinkTarget is not null)
            throw new SearchJudgeException("OutputExists");
        if (Directory.GetParent(command.Output) is not { Exists: true }) throw new SearchJudgeException("OutputParentUnavailable");
        if (command.TimeoutSeconds is < 1 or > 600) throw new SearchJudgeException("InvalidTimeout");
        var source = Dataset.Load(command.Input);
        var labelsBytes = File.ReadAllBytes(command.Labels);
        var dataset = ReadOnlyLabeledDataset.Join(source, Dataset.StrictUtf8.GetString(labelsBytes), command.ExpectedSha256);
        var rubricBytes = File.ReadAllBytes(command.Prompt);
        if (string.IsNullOrWhiteSpace(Dataset.StrictUtf8.GetString(rubricBytes).TrimStart('\uFEFF')))
            throw new SearchJudgeException("PromptEmpty");
        var provenance = new Provenance(1, "CalibrationNotHeldOut", dataset.DatasetSha256, Hash(labelsBytes),
            Hash(rubricBytes), command.Model, command.TimeoutSeconds, DateTimeOffset.UtcNow, null, "Running");
        var ordered = dataset.Rows.OrderBy(r => r.Identity.RunId, StringComparer.Ordinal).ThenBy(r => r.Identity.Ordinal)
            .ThenBy(r => r.Identity.ScenarioId, StringComparer.Ordinal).ToArray();

        Directory.CreateDirectory(command.Output);
        // CreateNew prevents any report overwrite, including a collision during reservation.
        // All handles are reserved before any invocation; partial reservation is never cleaned up silently.
        FileStream Reserve(string name) => new(Path.Combine(command.Output, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var predictionsFile = Reserve("predictions.jsonl");
        using var metricsFile = Reserve("metrics.json");
        using var disagreementsFile = Reserve("disagreements.md");
        using var provenanceFile = Reserve("run.json");
        var snapshot = Path.Combine(command.Output, "rubric.md");
        using (var rubricFile = Reserve("rubric.md"))
        {
            rubricFile.Write(rubricBytes);
            rubricFile.Flush(flushToDisk: true);
        }
        await WriteJson(provenanceFile, provenance);
        using var predictions = new StreamWriter(predictionsFile, new UTF8Encoding(false), leaveOpen: true);
        var rows = new List<SearchJudgePrediction>();
        foreach (var row in ordered)
        {
            var status = "NotAttempted";
            SearchResponseJudgment? judgment = null;
            string? error = "ExternalCancellation";
            if (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = await judge(row.Input, snapshot, ct);
                    ct.ThrowIfCancellationRequested();
                    // Keep the reusable delegate seam subject to exactly the same output contract.
                    judgment = SearchResponseJudgment.Parse(JsonSerializer.Serialize(result, Json));
                    status = "Success"; error = null;
                }
                catch (Exception) when (ct.IsCancellationRequested)
                { status = "Canceled"; error = "ExternalCancellation"; }
                catch (Exception e) { status = "Error"; error = SafeError(e); }
            }
            var prediction = new SearchJudgePrediction(row.Identity, row.Input.Question, row.Input.Answer,
                row.Input.Payload.GetProperty("responseEvidence"), row.Input.Payload.GetProperty("toolEvidence"),
                row.Human, status, judgment, error);
            rows.Add(prediction);
            // Cancellation must not suppress the active outcome or the remaining bookkeeping rows.
            await predictions.WriteLineAsync(JsonSerializer.Serialize(prediction, Json));
            await predictions.FlushAsync();
            predictionsFile.Flush(flushToDisk: true);
            await progress.WriteLineAsync($"{rows.Count}/{ordered.Length}: {status}");
        }
        var metrics = SearchJudgeMetrics.Calculate(dataset.SourceRowCount, rows);
        var runStatus = ct.IsCancellationRequested ? "Canceled" : metrics.Error > 0 ? "CompletedWithErrors" : "Completed";
        await WriteJson(metricsFile, metrics);
        using (var markdown = new StreamWriter(disagreementsFile, new UTF8Encoding(false), leaveOpen: true))
        {
            await markdown.WriteAsync(Disagreements(rows));
            await markdown.FlushAsync();
            disagreementsFile.Flush(flushToDisk: true);
        }
        await WriteJson(provenanceFile, provenance with { FinishedUtc = DateTimeOffset.UtcNow, Status = runStatus });
        await Totals(progress, metrics);
        return new(runStatus, metrics);
    }

    private static string SafeError(Exception error) => error switch
    {
        SearchJudgeException { Code: "NoAnswer" or "InputLimit" or "ResponseLimit" or "ResponseSchema" or
            "ResponseRefused" or "ResponseTruncated" or "ResponseEnvelope" or "ProviderFailure" or "Timeout" } known => known.Code,
        OperationCanceledException => "Timeout",
        _ => "JudgeFailed"
    };

    private static async Task WriteJson<T>(FileStream stream, T value)
    {
        stream.Position = 0; // Only our newly reserved report handle can be updated.
        await JsonSerializer.SerializeAsync(stream, value, Pretty);
        stream.SetLength(stream.Position);
        stream.Flush(flushToDisk: true);
    }

    private static string Disagreements(IEnumerable<SearchJudgePrediction> rows)
    {
        var result = new StringBuilder("# Calibration disagreements\n\nNot held-out evaluation. Only successful judgments are compared; errors are in predictions.jsonl.\n");
        var count = 0;
        foreach (var row in rows.Where(r => r.Status == "Success" && r.Judgment!.Label != r.Human.Label.ToLowerInvariant()))
        {
            result.Append("\n## Disagreement ").Append(++count).Append('\n');
            Block("Source identity", JsonSerializer.Serialize(row.Identity, Pretty));
            Block("Question", row.Question);
            Block("Answer", row.Answer ?? "(No answer captured)");
            Block("Human annotation", JsonSerializer.Serialize(row.Human, Pretty));
            Block("Judge label", row.Judgment!.Label);
            Block("Judge reasoning", row.Judgment.Reasoning);
        }
        if (count == 0) result.Append("\nNo successful human/judge disagreements.\n");
        return result.ToString();

        void Block(string title, string text)
        {
            result.Append("\n### ").Append(title).Append("\n\n");
            // Indented literal blocks preserve full text without trusting embedded Markdown fences/headings.
            foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
                result.Append("    ").Append(line).Append('\n');
        }
    }

    private static async Task Totals(TextWriter output, SearchJudgeMetrics m)
    {
        await output.WriteLineAsync("Calibration only; not held-out evaluation.");
        await output.WriteLineAsync($"Source={m.TotalSource} Labeled={m.TotalLabels} Unlabeled={m.Unlabeled} Attempted={m.Attempted} Success={m.Success} Error={m.Error} Canceled={m.Canceled} NotAttempted={m.NotAttempted}");
        await output.WriteLineAsync($"TP={m.TP} FP={m.FP} FN={m.FN} TN={m.TN} (bad is positive)");
        await output.WriteLineAsync("Label coverage: " + Display(m.LabelCoverage, "no source rows"));
        await output.WriteLineAsync("Judging coverage: " + Display(m.Coverage, "no labeled rows"));
        await output.WriteLineAsync("Accuracy: " + Display(m.Accuracy, "no successful judgments"));
        await output.WriteLineAsync("PrecisionBad: " + Display(m.PrecisionBad, "no predicted bad judgments among successes"));
        await output.WriteLineAsync("RecallBad: " + Display(m.RecallBad, "no human-bad rows among successes"));
    }

    private static string Display(double? ratio, string undefined) => ratio?.ToString("P2", CultureInfo.InvariantCulture) ?? $"undefined ({undefined})";
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
