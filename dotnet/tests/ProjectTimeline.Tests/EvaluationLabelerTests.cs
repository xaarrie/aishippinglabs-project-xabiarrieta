using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProjectTimeline.EvaluationLabeler;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class EvaluationLabelerTests
{
    [Fact]
    public void OneClickBadCreatesDurableCsv()
    {
        var fixture = new LabelerFixture();
        using var store = new LabelStore(fixture.Load(), fixture.Labels);
        var saved = store.Save(0, "Bad", "uncategorized", 0);
        Assert.Equal(1, saved.Revision);
        Assert.True(File.Exists(fixture.Labels), "A confirmed one-click label must exist on disk.");
        Assert.Contains("uncategorized", File.ReadAllText(fixture.Labels));
        Assert.Equal(1, store.Snapshot.Pending);
    }

    [Fact]
    public void OrdinaryMetadataExportsUnchangedForDirectJsonlIdentityJoin()
    {
        var f = new LabelerFixture();
        f.WriteRows(LabelerFixture.Row(1, "Which record is correct?"));
        var data = f.Load();
        using var store = new LabelStore(data, f.Labels);
        store.Save(0, "Good", "", 0);
        // These deliberately delimiter-free values need no CSV unescaping beyond their quotes.
        var cells = File.ReadLines(f.Labels).ElementAt(1).Split(',').Select(cell => cell.Trim('"')).ToArray();
        Assert.Equal(data.Records[0].RunId, cells[2]);
        Assert.Equal(data.Records[0].ScenarioId, cells[3]);
        Assert.Equal(data.Records[0].Question, cells[5]);
        Assert.Single(data.Records, record => record.RunId == cells[2] && record.ScenarioId == cells[3]);
    }

    [Fact]
    public void RestartRoundTripsOpaqueIdsUnicodeQuotedMultilineMetadataAndDuplicateQuestions()
    {
        var f = new LabelerFixture();
        var question = "\t=SUM(1,2)\r\n\"quoted\" café 日本語\nline";
        f.WriteRows(LabelerFixture.Row(1, question, run: "'=run,\r\nα"), LabelerFixture.Row(2, question));
        var data = f.Load();
        using (var store = new LabelStore(data, f.Labels))
        {
            store.Save(0, "Bad", "uncategorized", 0);
            Assert.Equal(1, store.Snapshot.Resume(data));
            store.Save(1, "Good", "", 0);
            Assert.Equal(0, store.Snapshot.Resume(data));
        }
        var csv = File.ReadAllText(f.Labels);
        Assert.StartsWith("\"version\",\"datasetSha256\"", csv);
        Assert.Contains("'\t=SUM", csv);
        Assert.DoesNotContain("Full answer", csv);
        using var resumed = new LabelStore(data, f.Labels);
        Assert.Equal(2, resumed.Snapshot.Labeled);
        Assert.Equal("Bad", resumed.Snapshot.Labels[0].Label);
        Assert.Equal("Good", resumed.Snapshot.Labels[1].Label);
        Assert.Equal(0, resumed.Snapshot.Resume(data));
        resumed.Save(0, "Bad", "wrong-citation", 1);
        Assert.Equal(0, resumed.Snapshot.Pending);
        resumed.Save(0, "Good", "", 2);
        Assert.Equal("", resumed.Snapshot.Labels[0].FailureCategory);
        Assert.Equal(3, resumed.Snapshot.Labels[0].Revision);
    }

    [Theory]
    [InlineData("café 日本語, \"quoted\"\r\nnext", "café 日本語, \"quoted\"\r\nnext")]
    [InlineData("  ordinary text", "  ordinary text")]
    [InlineData("'literal", "''literal")]
    [InlineData("''literal", "'''literal")]
    [InlineData("=SUM(1,2)", "'=SUM(1,2)")]
    [InlineData("+formula", "'+formula")]
    [InlineData("-formula", "'-formula")]
    [InlineData("@formula", "'@formula")]
    [InlineData(" \t=formula", "' \t=formula")]
    [InlineData("\ttext", "'\ttext")]
    [InlineData("\rtext", "'\rtext")]
    [InlineData("\ntext", "'\ntext")]
    [InlineData(" \u0001text", "' \u0001text")]
    public void MetadataEscapesOnlyUnsafeCellsAndResumesLegacyPrefixedVersionOne(string value, string encoded)
    {
        var f = new LabelerFixture();
        f.WriteRows(LabelerFixture.Row(1, value, run: value, scenario: value), LabelerFixture.Row(2));
        var data = f.Load();
        Annotation first;
        Annotation second;
        using (var store = new LabelStore(data, f.Labels))
        {
            first = store.Save(0, "Bad", "wrong-citation", 0);
            second = store.Save(1, "Good", "", 0);
        }
        Assert.Contains(AnnotationCsvRow(data, 0, encoded, encoded, encoded, first), File.ReadAllText(f.Labels));
        using (var resumed = new LabelStore(data, f.Labels))
        {
            Assert.Equal(first, resumed.Snapshot.Labels[0]);
            Assert.Equal(second, resumed.Snapshot.Labels[1]);
        }
        // Simulate the previous version-1 writer: every text cell carried an extra apostrophe.
        var header = File.ReadLines(f.Labels).First() + "\r\n";
        var legacy = header + AnnotationCsvRow(data, 0, "'" + value, "'" + value, "'" + value, first)
            + AnnotationCsvRow(data, 1, "'" + data.Records[1].RunId, "'" + data.Records[1].ScenarioId,
                "'" + data.Records[1].Question, second);
        File.WriteAllText(f.Labels, legacy, Dataset.StrictUtf8);
        using (var resumed = new LabelStore(data, f.Labels))
        {
            Assert.Equal(first, resumed.Snapshot.Labels[0]);
            Assert.Equal(second, resumed.Snapshot.Labels[1]);
            resumed.Save(0, "Good", "", first.Revision);
        }
        using var migrated = new LabelStore(data, f.Labels);
        Assert.Equal("", migrated.Snapshot.Labels[0].FailureCategory);
        Assert.Equal(first.Revision + 1, migrated.Snapshot.Labels[0].Revision);
        Assert.Equal(second, migrated.Snapshot.Labels[1]);
    }

    [Theory]
    [InlineData("=SUM(1,2)")]
    [InlineData("+formula")]
    [InlineData("-formula")]
    [InlineData("@formula")]
    [InlineData(" \t=formula")]
    [InlineData("\ttext")]
    [InlineData("\rtext")]
    [InlineData("\ntext")]
    [InlineData(" \u0001text")]
    public void UnprefixedUnsafeMetadataIsRejectedWithoutOverwritingLabels(string value)
    {
        foreach (var column in new[] { 2, 3, 5 })
        {
            var f = new LabelerFixture();
            f.WriteRows(LabelerFixture.Row(1, value, run: value, scenario: value));
            var data = f.Load();
            using (var store = new LabelStore(data, f.Labels)) store.Save(0, "Good", "", 0);
            var header = File.ReadLines(f.Labels).First() + "\r\n";
            string[] cells = ["1", data.Sha256, "'" + value, "'" + value, "1", "'" + value,
                "Good", "", "1", "2026-11-02T00:00:00.0000000+00:00"];
            cells[column] = value;
            File.WriteAllText(f.Labels, header + CsvLine(cells), Dataset.StrictUtf8);
            var bytes = File.ReadAllBytes(f.Labels);
            var ex = Assert.Throws<LabelerException>(() => new LabelStore(data, f.Labels));
            Assert.Equal("csv-metadata", ex.Code);
            Assert.Equal(2, ex.Line);
            Assert.Equal(bytes, File.ReadAllBytes(f.Labels));
        }
    }

    private static string AnnotationCsvRow(Dataset data, int index, string run, string scenario, string question, Annotation a)
        => CsvLine("1", data.Sha256, run, scenario, data.Records[index].Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
            question, a.Label, a.FailureCategory, a.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            a.UpdatedUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    private static string CsvLine(params string[] cells)
        => string.Join(",", cells.Select(cell => "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")) + "\r\n";

    [Fact]
    public void DatasetUsesExactBytesIncludingBomNotQuestionInputHash()
    {
        var f = new LabelerFixture();
        var row = LabelerFixture.Row(1, content: new string('x', 45001));
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(row + "\r\n")).ToArray();
        File.WriteAllBytes(f.Input, bytes);
        var data = f.Load();
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), data.Sha256);
        Assert.Equal(45001, data.Records[0].Content!.Length);
        Assert.Contains("Saved trace", data.Records[0].Detail("messages"));
        Assert.Equal("Partial", data.Records[0].ResearchStatus);
    }

    [Theory]
    [InlineData("{}", "input-required-runId")]
    [InlineData("[]", "input-object")]
    [InlineData("{", "input-json")]
    [InlineData("", "input-json")]
    public void MalformedRowsFailClosedWithSafeLineCode(string row, string code)
    {
        var f = new LabelerFixture();
        f.WriteRows(LabelerFixture.Row(1), row);
        var ex = Assert.Throws<LabelerException>(() => f.Load());
        Assert.Equal(code, ex.Code);
        Assert.Equal(2, ex.Line);
        Assert.DoesNotContain("Same question", ex.Message);
        Assert.False(File.Exists(f.Labels));
    }

    [Theory]
    [InlineData("\"ordinal\":1", "\"ordinal\":\"1\"", "input-ordinal")]
    [InlineData("\"question\":\"Same question\"", "\"question\":null", "input-required-question")]
    [InlineData("\"content\":\"Full answer\"", "\"content\":null", "input-content")]
    [InlineData("\"outcome\":\"Returned\"", "\"outcome\":null", "input-required-outcome")]
    [InlineData("\"status\":\"Partial\"", "\"status\":\"Partial\",\"status\":\"Complete\"", "input-duplicate-property")]
    public void InvalidRequiredTypesAndAmbiguousJsonRejected(string old, string replacement, string code)
    {
        var f = new LabelerFixture();
        f.WriteRows(LabelerFixture.Row(1).Replace(old, replacement, StringComparison.Ordinal));
        Assert.Equal(code, Assert.Throws<LabelerException>(() => f.Load()).Code);
    }

    [Fact]
    public void DuplicateIdentitiesAndInvalidUtf8AreRejected()
    {
        var f = new LabelerFixture();
        f.WriteRows(LabelerFixture.Row(1), LabelerFixture.Row(1));
        Assert.Equal("input-duplicate-identity", Assert.Throws<LabelerException>(() => f.Load()).Code);
        File.WriteAllBytes(f.Input, [0xC3, 0x28]);
        Assert.Equal("input-utf8", Assert.Throws<LabelerException>(() => f.Load()).Code);
    }

    [Theory]
    [InlineData("Good", "hallucination")]
    [InlineData("Bad", "")]
    [InlineData("Bad", "Hallucination")]
    [InlineData("Bad", "hallucination,other")]
    [InlineData("Unknown", "")]
    public void InvalidHumanLabelCombinationsNeverWrite(string label, string category)
    {
        var f = new LabelerFixture();
        using var store = new LabelStore(f.Load(), f.Labels);
        Assert.Equal("invalid-label-category", Assert.Throws<LabelerException>(() => store.Save(0, label, category, 0)).Code);
        Assert.False(File.Exists(f.Labels));
        Assert.Empty(store.Snapshot.Labels);
    }

    [Fact]
    public void EveryFixedCategoryIsEditableAndGoodClearsIt()
    {
        var f = new LabelerFixture();
        using var store = new LabelStore(f.Load(), f.Labels);
        long revision = 0;
        foreach (var category in LabelStore.Categories)
            Assert.Equal(category, store.Save(0, "Bad", category, revision++).FailureCategory);
        Assert.Equal("", store.Save(0, "Good", "", revision).FailureCategory);
    }

    [Fact]
    public void RevisionConflictAndImmutableSnapshotPreventLostLabels()
    {
        var f = new LabelerFixture();
        using var store = new LabelStore(f.Load(), f.Labels);
        var before = store.Snapshot;
        store.Save(0, "Good", "", 0);
        var bytes = File.ReadAllBytes(f.Labels);
        var conflict = Assert.Throws<LabelerException>(() => store.Save(0, "Bad", "other", 0));
        Assert.Equal(409, conflict.Status);
        Assert.Equal("revision-conflict", conflict.Code);
        Assert.Equal(bytes, File.ReadAllBytes(f.Labels));
        Assert.Empty(before.Labels);
        Assert.Equal("Good", store.Snapshot.Labels[0].Label);
    }

    [Fact]
    public async Task ConcurrentInProcessSavesSerializeAndRejectOneStaleWriter()
    {
        var f = new LabelerFixture();
        using var store = new LabelStore(f.Load(), f.Labels);
        async Task<string> Save(string label, string category) => await Task.Run(() =>
        {
            try { return store.Save(0, label, category, 0).Label; }
            catch (LabelerException ex) { return ex.Code; }
        });
        var results = await Task.WhenAll(Save("Good", ""), Save("Bad", "other"));
        Assert.Single(results, result => result == "revision-conflict");
        Assert.Equal(1, store.Snapshot.Labels[0].Revision);
    }

    [Fact]
    public void FailedAtomicCommitKeepsOldCsvAndMemoryAndCleansOnlyOwnedTemporaryFile()
    {
        var f = new LabelerFixture();
        var fail = false;
        using var store = new LabelStore(f.Load(), f.Labels, (source, target) =>
        {
            if (fail) throw new IOException("Synthetic failure");
            File.Move(source, target, true);
        });
        store.Save(0, "Good", "", 0);
        var old = store.Snapshot;
        var bytes = File.ReadAllBytes(f.Labels);
        var unrelated = System.IO.Path.Combine(f.Directory, "not-ours.tmp");
        File.WriteAllText(unrelated, "Retain");
        fail = true;
        Assert.Equal("save-failed", Assert.Throws<LabelerException>(() => store.Save(0, "Bad", "other", 1)).Code);
        Assert.Same(old, store.Snapshot);
        Assert.Equal(bytes, File.ReadAllBytes(f.Labels));
        Assert.Single(System.IO.Directory.GetFiles(f.Directory, "*.tmp"));
        Assert.Equal("Retain", File.ReadAllText(unrelated));
    }

    [Fact]
    public void ExternalCsvModificationIsNeverClobbered()
    {
        var f = new LabelerFixture();
        using var store = new LabelStore(f.Load(), f.Labels);
        store.Save(0, "Good", "", 0);
        File.AppendAllText(f.Labels, "External edit");
        var bytes = File.ReadAllBytes(f.Labels);
        Assert.Equal("external-change", Assert.Throws<LabelerException>(() => store.Save(1, "Good", "", 0)).Code);
        Assert.Equal(bytes, File.ReadAllBytes(f.Labels));
        Assert.Single(store.Snapshot.Labels);
    }

    [Fact]
    public void ExternalFileCreationBeforeFirstSaveIsNeverClobbered()
    {
        var f = new LabelerFixture();
        using var store = new LabelStore(f.Load(), f.Labels);
        File.WriteAllText(f.Labels, "External annotations");
        Assert.Equal("external-change", Assert.Throws<LabelerException>(() => store.Save(0, "Good", "", 0)).Code);
        Assert.Equal("External annotations", File.ReadAllText(f.Labels));
    }

    [Fact]
    public async Task SidecarBlocksSecondProcessButItsPresenceDoesNotBlockRestart()
    {
        var f = new LabelerFixture();
        using (var store = new LabelStore(f.Load(), f.Labels))
        {
            Assert.Equal("labels-writer-active", Assert.Throws<LabelerException>(() => new LabelStore(f.Load(), f.Labels)).Code);
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { typeof(LabelerApp).Assembly.Location, "--input", f.Input, "--labels", f.Labels }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(1, process.ExitCode);
            Assert.Contains("labels-writer-active", await error);
        }
        Assert.True(File.Exists(f.Labels + ".lock"));
        using var restarted = new LabelStore(f.Load(), f.Labels);
        Assert.Empty(restarted.Snapshot.Labels);
    }

    [Theory]
    [InlineData("dataset", "csv-dataset")]
    [InlineData("duplicate", "csv-duplicate-identity")]
    [InlineData("schema", "csv-schema")]
    [InlineData("quote", "csv-unclosed-quote")]
    [InlineData("revision", "csv-revision")]
    [InlineData("label", "csv-label-category")]
    [InlineData("category", "csv-label-category")]
    [InlineData("identity", "csv-identity")]
    [InlineData("question", "csv-record")]
    [InlineData("time", "csv-time")]
    public void ResumeValidatesWholeCsvWithoutChangingIt(string mutation, string code)
    {
        var f = new LabelerFixture();
        using (var store = new LabelStore(f.Load(), f.Labels)) store.Save(0, "Good", "", 0);
        var csv = File.ReadAllText(f.Labels);
        csv = mutation switch
        {
            "dataset" => csv.Replace(f.Load().Sha256, new string('0', 64), StringComparison.Ordinal),
            "duplicate" => csv + csv.Split("\r\n")[1] + "\r\n",
            "schema" => "question,label\r\nunknown,Good\r\n",
            "quote" => csv + "\"unfinished",
            "revision" => csv.Replace("\"Good\",\"\",\"1\"", "\"Good\",\"\",\"0\"", StringComparison.Ordinal),
            "label" => csv.Replace("\"Good\"", "\"Automatic\"", StringComparison.Ordinal),
            "category" => csv.Replace("\"Good\",\"\"", "\"Good\",\"other\"", StringComparison.Ordinal),
            "identity" => csv.Replace("opaque-run:0001", "unknown", StringComparison.Ordinal),
            "question" => csv.Replace("Same question", "Changed", StringComparison.Ordinal),
            "time" => csv.Replace("+00:00", "+01:00", StringComparison.Ordinal),
            _ => throw new InvalidOperationException()
        };
        File.WriteAllText(f.Labels, csv, Dataset.StrictUtf8);
        var ex = Assert.Throws<LabelerException>(() => new LabelStore(f.Load(), f.Labels));
        Assert.Equal(code, ex.Code);
        Assert.True(ex.Line > 0);
        Assert.Equal(csv, File.ReadAllText(f.Labels));
    }

    [Fact]
    public void SameQuestionFileWithDifferentResultBytesIsDifferentDataset()
    {
        var f = new LabelerFixture();
        using (var store = new LabelStore(f.Load(), f.Labels)) store.Save(0, "Good", "", 0);
        f.WriteRows(LabelerFixture.Row(1, content: "Changed response"), LabelerFixture.Row(2));
        Assert.Equal("csv-dataset", Assert.Throws<LabelerException>(() => new LabelStore(f.Load(), f.Labels)).Code);
    }

    [Fact]
    public void InputCollisionFailsWithoutChangingInput()
    {
        var f = new LabelerFixture();
        var bytes = File.ReadAllBytes(f.Input);
        Assert.Equal("labels-input-collision", Assert.Throws<LabelerException>(() => new LabelStore(f.Load(), f.Input)).Code);
        Assert.Equal(bytes, File.ReadAllBytes(f.Input));
    }

    [Fact]
    public void CliDefaultsAndOverridesAreExplicit()
    {
        var options = LabelerOptions.Parse(["--input", "example.jsonl"]);
        Assert.Equal(System.IO.Path.GetFullPath("example.labels.csv"), options.Labels);
        Assert.Equal(5198, options.Port);
        Assert.Equal(5200, LabelerOptions.Parse(["--input", "example.jsonl", "--port", "5200"]).Port);
    }

    [Theory]
    [InlineData("--port", "0")]
    [InlineData("--port", "65536")]
    [InlineData("--port", "bad")]
    [InlineData("--urls", "http://*:5198")]
    [InlineData("--input", "duplicate")]
    public void InvalidCliCannotExpandServerScope(string option, string value)
        => Assert.Throws<LabelerException>(() => LabelerOptions.Parse(["--input", "test.jsonl", option, value]));
}

internal sealed class LabelerFixture
{
    // Unique, synthetic, retained test artifacts; never write beside a real evaluation dataset.
    public string Directory { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "labeler-tests", Guid.NewGuid().ToString("N"));
    public string Input => System.IO.Path.Combine(Directory, "input.jsonl");
    public string Labels => System.IO.Path.Combine(Directory, "labels.csv");
    public LabelerFixture()
    {
        System.IO.Directory.CreateDirectory(Directory);
        WriteRows(Row(1), Row(2));
    }
    public static string Row(int ordinal, string question = "Same question", string? content = "Full answer", string? run = null, string? scenario = null)
        => JsonSerializer.Serialize(new
        {
            runId = run ?? "opaque-run", scenarioId = scenario ?? $"opaque-run:{ordinal:D4}",
            scenario = new { ordinal, question }, outcome = content is null ? "Failed" : "Returned",
            response = content is null ? null : new { content, status = "Partial" },
            messages = new[] { new { role = "assistant", text = "Saved trace <script>" } },
            error = content is null ? new { code = "LegacyFailure" } : null
        });
    public void WriteRows(params string[] rows) => File.WriteAllText(Input, string.Join("\n", rows) + "\n", Dataset.StrictUtf8);
    public Dataset Load() => Dataset.Load(Input);
}
