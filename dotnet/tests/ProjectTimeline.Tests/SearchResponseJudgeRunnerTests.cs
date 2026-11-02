using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProjectTimeline.SearchFactsJudge;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class SearchResponseJudgeRunnerTests
{
    private static SearchJudgePrediction Prediction(string human, string? judge, string status = "Success") => new(
        new("hash", "run", "scenario", 1), "Question", "Answer", JsonSerializer.SerializeToElement(new { }),
        JsonSerializer.SerializeToElement(Array.Empty<object>()), new(human, human == "Bad" ? "hallucination" : "", 1, DateTimeOffset.UnixEpoch),
        status, judge is null ? null : new("Grounded rationale", judge), status == "Error" ? "ResponseSchema" : null);

    [Fact]
    public void Bad_is_positive_and_only_successes_enter_metric_denominators()
    {
        var metrics = SearchJudgeMetrics.Calculate(8, [Prediction("Bad", "bad"), Prediction("Bad", "bad"),
            Prediction("Good", "bad"), Prediction("Bad", "good"), Prediction("Good", "good"), Prediction("Bad", null, "Error")]);
        Assert.Equal(8, metrics.TotalSource);
        Assert.Equal(6, metrics.TotalLabels);
        Assert.Equal(2, metrics.Unlabeled);
        Assert.Equal(5, metrics.Success);
        Assert.Equal(1, metrics.Error);
        Assert.Equal((2, 1, 1, 1), (metrics.TP, metrics.FP, metrics.FN, metrics.TN));
        Assert.Equal(0.6, metrics.Accuracy!.Value, 10);
        Assert.Equal(2.0 / 3, metrics.PrecisionBad!.Value, 10);
        Assert.Equal(2.0 / 3, metrics.RecallBad!.Value, 10);
        Assert.Equal(5.0 / 6, metrics.Coverage!.Value, 10);
    }

    [Fact]
    public void No_successes_have_undefined_accuracy_not_zero_measured_accuracy()
    {
        var metrics = SearchJudgeMetrics.Calculate(1, [Prediction("Bad", null, "Error")]);
        Assert.Null(metrics.Accuracy);
        Assert.Null(metrics.PrecisionBad);
        Assert.Null(metrics.RecallBad);
        Assert.Equal(0, metrics.Coverage);
        var empty = SearchJudgeMetrics.Calculate(0, []);
        Assert.Null(empty.Accuracy);
        Assert.Null(empty.Coverage);
    }

    [Fact]
    public void Good_only_successes_leave_bad_precision_and_recall_undefined()
    {
        var metrics = SearchJudgeMetrics.Calculate(2, [Prediction("Good", "good"), Prediction("Good", "good")]);
        Assert.Equal(1, metrics.Accuracy);
        Assert.Null(metrics.PrecisionBad);
        Assert.Null(metrics.RecallBad);
        Assert.Equal((0, 0, 0, 2), (metrics.TP, metrics.FP, metrics.FN, metrics.TN));
    }

    // All fixtures/reports live in the authorized generated-artifact tree, never beside real inputs.
    private sealed class Fixture
    {
        public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "runner-fixtures", Guid.NewGuid().ToString("N"));
        public string Input => Path.Combine(Root, "source.jsonl");
        public string Labels => Path.Combine(Root, "source.labels.csv");
        public string Prompt => Path.Combine(Root, "prompt.md");
        public string Output => Path.Combine(Root, "report");
        public string[] Args => ["--input", Input, "--output", Output, "--prompt", Prompt];
        public Fixture(params string[] humanLabels)
        {
            Directory.CreateDirectory(Root);
            var source = string.Concat(Enumerable.Range(1, humanLabels.Length + 1).Select(i => JsonSerializer.Serialize(new
            {
                runId = "run", scenarioId = "scenario-" + i, outcome = "Returned",
                scenario = new { ordinal = i, question = "Full question " + i + "\nsecond line" },
                response = new { content = "Full answer " + i + "\n```untrusted markdown```", facts = new[] {
                    new { id = "fact-" + i, claim = "Source claim " + i, clarificationEvidence = "Source clarification" } } }
            }) + "\n"));
            File.WriteAllText(Input, source, new UTF8Encoding(false));
            var hash = Sha(File.ReadAllBytes(Input));
            static string Csv(IEnumerable<string> cells) => string.Join(",", cells.Select(c => "\"" + c.Replace("\"", "\"\"") + "\"")) + "\r\n";
            var header = Csv(["version", "datasetSha256", "runId", "scenarioId", "ordinal", "question", "label", "failureCategory", "revision", "updatedUtc"]);
            // Reverse CSV order so the runner must establish deterministic identity/ordinal order.
            var labels = string.Concat(Enumerable.Range(1, humanLabels.Length).Reverse().Select(i => Csv(["1", hash, "run", "scenario-" + i,
                i.ToString(System.Globalization.CultureInfo.InvariantCulture), "'Full question " + i + "\nsecond line", humanLabels[i - 1],
                humanLabels[i - 1] == "Bad" ? "hallucination" : "", "1", "2026-11-02T21:00:00.0000000+00:00"])));
            File.WriteAllText(Labels, header + labels, new UTF8Encoding(false));
            File.WriteAllText(Prompt, "# Exact calibration rubric\nGeneral evidence principles.\n", new UTF8Encoding(false));
        }
        public SearchJudgeCommand Command => SearchJudgeCommand.Parse(Args);
        public SearchJudgePrediction[] Predictions => File.ReadLines(Path.Combine(Output, "predictions.jsonl"))
            .Select(line => JsonSerializer.Deserialize<SearchJudgePrediction>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToArray();
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static SearchResponseJudgment Result(string label) => new("Full grounded reasoning\nwith evidence", label);

    [Fact]
    public async Task Runner_exports_complete_disagreements_provenance_and_metrics_without_mutating_inputs()
    {
        var f = new Fixture("Bad", "Good", "Bad", "Good");
        var source = File.ReadAllBytes(f.Input); var labels = File.ReadAllBytes(f.Labels); var prompt = File.ReadAllBytes(f.Prompt);
        var seen = new List<string>();
        var progress = new StringWriter();
        var result = await SearchJudgeRunner.RunAsync(f.Command, (input, snapshot, _) =>
        {
            Assert.Equal(prompt, File.ReadAllBytes(snapshot));
            Assert.True(File.Exists(Path.Combine(f.Output, "metrics.json")));
            Assert.True(File.Exists(Path.Combine(f.Output, "run.json")));
            Assert.True(File.Exists(Path.Combine(f.Output, "disagreements.md")));
            if (seen.Count > 0)
            {
                using var reader = new StreamReader(new FileStream(Path.Combine(f.Output, "predictions.jsonl"),
                    FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                Assert.Equal(seen.Count, reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            }
            seen.Add(input.Question);
            return Task.FromResult(Result(seen.Count <= 2 ? "bad" : "good"));
        }, progress);
        Assert.Equal(0, result.ExitCode); // Disagreements alone never fail a run.
        Assert.Equal((1, 1, 1, 1), (result.Metrics.TP, result.Metrics.FP, result.Metrics.FN, result.Metrics.TN));
        Assert.Equal(Enumerable.Range(1, 4).Select(i => "Full question " + i + "\nsecond line"), seen);
        Assert.Equal(4, f.Predictions.Length);
        Assert.All(f.Predictions, row => Assert.Contains("Source clarification", row.ResponseEvidence.GetRawText()));
        var markdown = File.ReadAllText(Path.Combine(f.Output, "disagreements.md"));
        foreach (var i in new[] { 2, 3 })
        {
            Assert.Contains("scenario-" + i, markdown);
            Assert.Contains("Full question " + i, markdown);
            Assert.Contains("Full answer " + i, markdown);
            Assert.Contains("\"ordinal\": " + i, markdown);
        }
        Assert.Contains("\"runId\": \"run\"", markdown);
        Assert.Contains("hallucination", markdown);
        Assert.Contains("Full grounded reasoning", markdown);
        Assert.DoesNotContain("scenario-1", markdown);
        Assert.DoesNotContain("scenario-4", markdown);
        using var provenance = JsonDocument.Parse(File.ReadAllText(Path.Combine(f.Output, "run.json")));
        Assert.Equal(Sha(source), provenance.RootElement.GetProperty("datasetSha256").GetString());
        Assert.Equal(Sha(labels), provenance.RootElement.GetProperty("labelsSha256").GetString());
        Assert.Equal(Sha(prompt), provenance.RootElement.GetProperty("promptSha256").GetString());
        Assert.Equal("gpt-4o-mini", provenance.RootElement.GetProperty("model").GetString());
        Assert.Equal("Completed", provenance.RootElement.GetProperty("status").GetString());
        Assert.Equal(prompt, File.ReadAllBytes(Path.Combine(f.Output, "rubric.md")));
        using var metrics = JsonDocument.Parse(File.ReadAllText(Path.Combine(f.Output, "metrics.json")));
        Assert.Equal(0.5, metrics.RootElement.GetProperty("accuracy").GetDouble());
        Assert.Equal("CalibrationNotHeldOut", metrics.RootElement.GetProperty("evaluationKind").GetString());
        Assert.Contains("4/4: Success", progress.ToString());
        Assert.Contains("Unlabeled=1", progress.ToString());
        Assert.Equal(source, File.ReadAllBytes(f.Input));
        Assert.Equal(labels, File.ReadAllBytes(f.Labels));
        Assert.Equal(prompt, File.ReadAllBytes(f.Prompt));
        Assert.False(File.Exists(f.Labels + ".lock"));
    }

    [Fact]
    public async Task Row_errors_are_sanitized_excluded_and_do_not_stop_later_attempts()
    {
        const string secret = "PROVIDER_SECRET_SENTINEL";
        var f = new Fixture("Bad", "Good", "Bad", "Good");
        var calls = 0; var progress = new StringWriter();
        var result = await SearchJudgeRunner.RunAsync(f.Command, (_, _, _) => ++calls switch
        {
            1 => Task.FromException<SearchResponseJudgment>(new SearchJudgeException("ResponseSchema")),
            2 => Task.FromException<SearchResponseJudgment>(new InvalidOperationException(secret)),
            3 => Task.FromException<SearchResponseJudgment>(new SearchJudgeException(secret)),
            _ => Task.FromResult(Result("good"))
        }, progress);
        Assert.Equal(4, calls);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal((1, 3), (result.Metrics.Success, result.Metrics.Error));
        Assert.Equal(1, result.Metrics.Accuracy);
        Assert.Equal(0.25, result.Metrics.Coverage);
        Assert.Equal(new[] { "ResponseSchema", "JudgeFailed", "JudgeFailed", null }, f.Predictions.Select(r => r.ErrorCode));
        Assert.All(f.Predictions.Take(3), row => Assert.Null(row.Judgment));
        Assert.DoesNotContain("scenario-1", File.ReadAllText(Path.Combine(f.Output, "disagreements.md")));
        Assert.DoesNotContain(secret, progress.ToString());
        foreach (var path in Directory.GetFiles(f.Output)) Assert.DoesNotContain(secret, File.ReadAllText(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_accounts_for_every_label_without_starting_later_rows(bool beforeFirst)
    {
        var f = new Fixture("Good", "Bad", "Good");
        using var cancellation = new CancellationTokenSource();
        if (beforeFirst) cancellation.Cancel();
        var calls = 0;
        var result = await SearchJudgeRunner.RunAsync(f.Command, (_, _, ct) =>
        {
            calls++; cancellation.Cancel();
            return Task.FromCanceled<SearchResponseJudgment>(ct);
        }, new StringWriter(), cancellation.Token);
        Assert.Equal(beforeFirst ? 0 : 1, calls);
        Assert.Equal(beforeFirst ? 0 : 1, result.Metrics.Canceled);
        Assert.Equal(beforeFirst ? 3 : 2, result.Metrics.NotAttempted);
        Assert.Equal(calls, result.Metrics.Attempted);
        Assert.Equal(0, result.Metrics.Error);
        Assert.Equal(3, f.Predictions.Length);
        Assert.Null(result.Metrics.Accuracy);
        Assert.Equal("Canceled", result.Status);
        Assert.Equal(1, result.ExitCode);
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("file")]
    [InlineData("hash")]
    [InlineData("blank-prompt")]
    public async Task Preflight_failures_block_calls_and_preserve_existing_output(string failure)
    {
        var f = new Fixture("Good"); var command = f.Command;
        if (failure == "directory") Directory.CreateDirectory(f.Output);
        if (failure == "file") File.WriteAllText(f.Output, "existing-report");
        if (failure == "hash") command = command with { ExpectedSha256 = new string('0', 64) };
        if (failure == "blank-prompt") File.WriteAllText(f.Prompt, " ");
        var calls = 0;
        await Assert.ThrowsAnyAsync<Exception>(() => SearchJudgeRunner.RunAsync(command, (_, _, _) =>
        { calls++; return Task.FromResult(Result("good")); }, new StringWriter()));
        Assert.Equal(0, calls);
        if (failure == "directory") Assert.Empty(Directory.GetFiles(f.Output));
        else if (failure == "file") Assert.Equal("existing-report", File.ReadAllText(f.Output));
        else Assert.False(Directory.Exists(f.Output));
    }

    [Fact]
    public async Task No_successes_display_undefined_metrics_and_invalid_fake_results_are_errors()
    {
        var f = new Fixture("Bad"); var progress = new StringWriter();
        var result = await SearchJudgeRunner.RunAsync(f.Command, (_, _, _) => Task.FromResult(Result("invalid")), progress);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("ResponseSchema", Assert.Single(f.Predictions).ErrorCode);
        Assert.Contains("Accuracy: undefined (no successful judgments)", progress.ToString());
        using var metrics = JsonDocument.Parse(File.ReadAllText(Path.Combine(f.Output, "metrics.json")));
        Assert.Equal(JsonValueKind.Null, metrics.RootElement.GetProperty("accuracy").ValueKind);
    }

    [Fact]
    public async Task Help_and_missing_key_never_create_output_or_load_credentials_from_files()
    {
        var output = new StringWriter(); var error = new StringWriter();
        Assert.Equal(0, await SearchJudgeConsole.RunAsync(["--help"], output, error, () => throw new Exception("must not read credentials")));
        Assert.Contains("--input", output.ToString());
        Assert.Contains("--output", output.ToString());
        var f = new Fixture("Good");
        foreach (var missing in new string?[] { null, "", " " })
        {
            error.GetStringBuilder().Clear();
            Assert.Equal(2, await SearchJudgeConsole.RunAsync(f.Args, new StringWriter(), error, () => missing));
            Assert.Contains("OPENAI_API_KEY", error.ToString());
            Assert.Contains("no output was created", error.ToString());
            Assert.False(Directory.Exists(f.Output));
        }
        var secret = "DO_NOT_PRINT_SECRET";
        error.GetStringBuilder().Clear();
        Assert.Equal(2, await SearchJudgeConsole.RunAsync(["--unknown", secret], new StringWriter(), error, () => secret));
        Assert.DoesNotContain(secret, error.ToString());
    }

    [Fact]
    public void Cli_defaults_and_explicit_settings_are_exact_and_unknown_or_duplicate_flags_fail()
    {
        var command = SearchJudgeCommand.Parse(["--input", "source.jsonl", "--output", "new-report"]);
        Assert.EndsWith("source.labels.csv", command.Labels);
        Assert.Equal("gpt-4o-mini", command.Model);
        Assert.Equal(60, command.TimeoutSeconds);
        var configured = SearchJudgeCommand.Parse(["--input", "source.jsonl", "--output", "new-report", "--labels", "human.csv",
            "--model", "chosen-model", "--prompt", "rubric.md", "--expected-sha256", new string('A', 64), "--timeout-seconds", "12"]);
        Assert.Equal("chosen-model", configured.Model);
        Assert.Equal(12, configured.TimeoutSeconds);
        Assert.Equal(new string('a', 64), configured.ExpectedSha256);
        Assert.Throws<ArgumentException>(() => SearchJudgeCommand.Parse(["--input", "source.jsonl", "--output", "x", "--output", "y"]));
        Assert.Throws<ArgumentException>(() => SearchJudgeCommand.Parse(["--input", "source.jsonl", "--output", "x", "--timeout-seconds", "0"]));
    }
}
