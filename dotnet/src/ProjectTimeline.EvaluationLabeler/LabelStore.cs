using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using InputDataset = ProjectTimeline.EvaluationLabeler.Dataset;

namespace ProjectTimeline.EvaluationLabeler;

public sealed record Annotation(string Label, string FailureCategory, long Revision, DateTimeOffset UpdatedUtc);
public sealed record LabelSnapshot(ImmutableDictionary<int, Annotation> Labels)
{
    public int Labeled => Labels.Count;
    public int Pending => Labels.Values.Count(a => a.Label == "Bad" && a.FailureCategory == "uncategorized");
    public int Resume(Dataset dataset)
    {
        foreach (var record in dataset.Records) if (!Labels.ContainsKey(record.Index)) return record.Index;
        foreach (var record in dataset.Records)
            if (Labels[record.Index].FailureCategory == "uncategorized") return record.Index;
        return 0;
    }
}

public sealed class LabelStore : IDisposable
{
    public static ImmutableArray<string> Categories { get; } = ["uncategorized", "hallucination", "wrong-scope",
        "incomplete", "incorrect-answer", "wrong-citation", "unnecessary-refusal", "technical-error", "other"];
    private static readonly string[] Header = ["version", "datasetSha256", "runId", "scenarioId", "ordinal",
        "question", "label", "failureCategory", "revision", "updatedUtc"];
    private readonly object gate = new();
    private readonly FileStream processLock;
    private readonly Action<string, string> commitFile;
    private LabelSnapshot snapshot = new(ImmutableDictionary<int, Annotation>.Empty);
    private string? diskHash;
    private bool disposed;
    public Dataset Dataset { get; }
    public string Path { get; }
    public LabelSnapshot Snapshot => Volatile.Read(ref snapshot);

    // The optional atomic-commit seam permits deterministic IO-failure tests, never HTTP configuration.
    public LabelStore(Dataset dataset, string path, Action<string, string>? commitFile = null)
    {
        Dataset = dataset;
        Path = System.IO.Path.GetFullPath(path);
        this.commitFile = commitFile ?? ((source, target) => File.Move(source, target, true));
        ValidatePaths(dataset.Path, Path);
        try { processLock = new FileStream(Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new LabelerException("labels-writer-active"); }
        try
        {
            if (File.Exists(Path))
            {
                var bytes = File.ReadAllBytes(Path);
                diskHash = Hash(bytes);
                snapshot = new(ReadLabels(bytes));
            }
        }
        catch { processLock.Dispose(); throw; }
    }

    private static void ValidatePaths(string input, string labels)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(input, labels, comparison) || string.Equals(input, labels + ".lock", comparison))
            throw new LabelerException("labels-input-collision");
        if (!labels.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) throw new LabelerException("labels-extension");
        if (!Directory.Exists(System.IO.Path.GetDirectoryName(labels))) throw new LabelerException("labels-directory");
        // Reject link aliases rather than accidentally acquiring a different lock for the same target.
        foreach (var path in new[] { input, labels, labels + ".lock" })
        {
            if (Directory.Exists(path)) throw new LabelerException("labels-path-directory");
            var info = new FileInfo(path);
            if (info.LinkTarget is not null) throw new LabelerException("labels-path-link");
            for (var parent = info.Directory; parent is not null; parent = parent.Parent)
                if (parent.LinkTarget is not null) throw new LabelerException("labels-path-link");
        }
    }

    public Annotation Save(int record, string label, string category, long expectedRevision)
    {
        ValidateLabel(label, category);
        if (record < 0 || record >= Dataset.Records.Length || expectedRevision < 0)
            throw new LabelerException("invalid-selection");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var old = Snapshot;
            var revision = old.Labels.TryGetValue(record, out var prior) ? prior.Revision : 0;
            if (revision != expectedRevision) throw new LabelerException("revision-conflict", status: 409);
            if (revision == long.MaxValue) throw new LabelerException("revision-limit", status: 409);
            EnsureUnchanged();
            var annotation = new Annotation(label, category, revision + 1, DateTimeOffset.UtcNow);
            var next = new LabelSnapshot(old.Labels.SetItem(record, annotation));
            var bytes = InputDataset.StrictUtf8.GetBytes(Serialize(next));
            var nextHash = Hash(bytes);
            var temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var owned = false;
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    owned = true;
                    stream.Write(bytes);
                    stream.Flush(true);
                }
                EnsureUnchanged();
                commitFile(temporary, Path);
                owned = false;
                // There must be no fallible IO between the atomic replacement and publishing memory.
                diskHash = nextHash;
                Volatile.Write(ref snapshot, next);
                return annotation;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new LabelerException("save-failed", status: 503);
            }
            finally
            {
                if (owned)
                {
                    try { File.Delete(temporary); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { /* Only our temporary file may remain; the old CSV is never removed. */ }
                }
            }
        }
    }

    public static void ValidateLabel(string label, string category)
    {
        if (label == "Good" && category == "") return;
        if (label == "Bad" && Categories.Contains(category)) return;
        throw new LabelerException("invalid-label-category");
    }

    public byte[] DownloadCsv()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return EnsureUnchanged() ?? throw new LabelerException("labels-not-saved", status: 404);
        }
    }

    private byte[]? EnsureUnchanged()
    {
        try
        {
            var bytes = File.Exists(Path) ? File.ReadAllBytes(Path) : null;
            var current = bytes is null ? null : Hash(bytes);
            if (diskHash != current || Directory.Exists(Path)) throw new LabelerException("external-change", status: 409);
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new LabelerException("labels-unreadable", status: 503); }
    }

    private string Serialize(LabelSnapshot state)
    {
        var result = new StringBuilder(Csv.Row(Header));
        foreach (var record in Dataset.Records)
        {
            if (!state.Labels.TryGetValue(record.Index, out var a)) continue;
            result.Append(Csv.Row(["1", Dataset.Sha256, Csv.Metadata(record.RunId), Csv.Metadata(record.ScenarioId),
                record.Ordinal.ToString(CultureInfo.InvariantCulture), Csv.Metadata(record.Question), a.Label,
                a.FailureCategory, a.Revision.ToString(CultureInfo.InvariantCulture), a.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture)]));
        }
        return result.ToString();
    }

    private ImmutableDictionary<int, Annotation> ReadLabels(byte[] bytes)
    {
        string text;
        try { text = InputDataset.StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw new LabelerException("csv-utf8"); }
        var rows = Csv.Parse(text);
        if (rows.Count < 2 || !rows[0].Cells.SequenceEqual(Header)) throw new LabelerException("csv-schema", 1);
        var records = Dataset.Records.ToDictionary(r => (r.RunId, r.ScenarioId));
        var result = ImmutableDictionary.CreateBuilder<int, Annotation>();
        foreach (var (line, cells) in rows.Skip(1))
        {
            if (cells.Length != Header.Length || cells[0] != "1") throw new LabelerException("csv-schema", line);
            if (cells[1] != Dataset.Sha256) throw new LabelerException("csv-dataset", line);
            var key = (Csv.DecodeMetadata(cells[2], line), Csv.DecodeMetadata(cells[3], line));
            if (!records.TryGetValue(key, out var record)) throw new LabelerException("csv-identity", line);
            if (cells[4] != record.Ordinal.ToString(CultureInfo.InvariantCulture) || Csv.DecodeMetadata(cells[5], line) != record.Question)
                throw new LabelerException("csv-record", line);
            try { ValidateLabel(cells[6], cells[7]); }
            catch (LabelerException) { throw new LabelerException("csv-label-category", line); }
            if (!long.TryParse(cells[8], NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 1)
                throw new LabelerException("csv-revision", line);
            if (!DateTimeOffset.TryParseExact(cells[9], "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                || time.Offset != TimeSpan.Zero) throw new LabelerException("csv-time", line);
            if (!result.TryAdd(record.Index, new(cells[6], cells[7], revision, time)))
                throw new LabelerException("csv-duplicate-identity", line);
        }
        return result.ToImmutable();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            processLock.Dispose(); // A persistent sidecar file is not evidence of an active writer.
        }
    }
}
