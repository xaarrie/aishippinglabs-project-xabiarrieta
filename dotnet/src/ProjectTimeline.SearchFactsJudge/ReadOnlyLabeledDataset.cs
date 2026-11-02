using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using ProjectTimeline.EvaluationLabeler;

namespace ProjectTimeline.SearchFactsJudge;

public sealed record SearchResponseIdentity(string DatasetSha256, string RunId, string ScenarioId, int Ordinal);
public sealed record HumanSearchAnnotation(string Label, string FailureCategory, long Revision, DateTimeOffset UpdatedUtc);
public sealed record LabeledSearchResponse(SearchResponseIdentity Identity, HumanSearchAnnotation Human, SearchJudgeInput Input);
public sealed record LabeledSearchDataset(string DatasetPath, string DatasetSha256, int SourceRowCount,
    ImmutableArray<LabeledSearchResponse> Rows);

/// <summary>Reads exactly the two supplied files; never opens LabelStore, locks, or output files.</summary>
public static class ReadOnlyLabeledDataset
{
    private static readonly string[] Header = ["version", "datasetSha256", "runId", "scenarioId", "ordinal",
        "question", "label", "failureCategory", "revision", "updatedUtc"];

    public static LabeledSearchDataset Load(string jsonlPath, string labelsPath, string? expectedDatasetSha256 = null)
    {
        var dataset = Dataset.Load(jsonlPath);
        string csv;
        try { csv = Dataset.StrictUtf8.GetString(File.ReadAllBytes(labelsPath)); }
        catch (DecoderFallbackException) { throw new LabelerException("csv-utf8"); }
        return Join(dataset, csv, expectedDatasetSha256);
    }

    // In-memory seam for deterministic validation without creating fixtures beside real labels.
    public static LabeledSearchDataset Join(Dataset dataset, string csv, string? expectedDatasetSha256 = null)
    {
        if (expectedDatasetSha256 is not null && !string.Equals(dataset.Sha256, expectedDatasetSha256, StringComparison.Ordinal))
            throw new LabelerException("csv-dataset");
        var parsed = Csv.Parse(csv);
        if (parsed.Count == 0 || !parsed[0].Cells.SequenceEqual(Header)) throw new LabelerException("csv-schema", 1);
        var records = new Dictionary<(string, string), EvaluationRecord>();
        foreach (var record in dataset.Records)
            if (!records.TryAdd((record.RunId, record.ScenarioId), record)) throw new LabelerException("input-duplicate-identity");
        var seen = new HashSet<(string, string)>();
        var rows = ImmutableArray.CreateBuilder<LabeledSearchResponse>();
        foreach (var (line, cells) in parsed.Skip(1))
        {
            if (cells.Length != Header.Length || cells[0] != "1") throw new LabelerException("csv-schema", line);
            if (cells[1] != dataset.Sha256) throw new LabelerException("csv-dataset", line);
            // The labeler's v1 codec removes exactly ONE reversible prefix on these three columns.
            // In particular, never TrimStart apostrophes or apply spreadsheet decoding to source JSON.
            var key = (Csv.DecodeMetadata(cells[2], line), Csv.DecodeMetadata(cells[3], line));
            if (!records.TryGetValue(key, out var record)) throw new LabelerException("csv-identity", line);
            if (cells[4] != record.Ordinal.ToString(CultureInfo.InvariantCulture)
                || Csv.DecodeMetadata(cells[5], line) != record.Question) throw new LabelerException("csv-record", line);
            if (!seen.Add(key)) throw new LabelerException("csv-duplicate-identity", line);
            // Static validation does not construct the writable store or acquire its sidecar lock.
            try { LabelStore.ValidateLabel(cells[6], cells[7]); }
            catch (LabelerException) { throw new LabelerException("csv-label-category", line); }
            if (!long.TryParse(cells[8], NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 1)
                throw new LabelerException("csv-revision", line);
            if (!DateTimeOffset.TryParseExact(cells[9], "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var updated)
                || updated.Offset != TimeSpan.Zero) throw new LabelerException("csv-time", line);
            rows.Add(new(new(dataset.Sha256, record.RunId, record.ScenarioId, record.Ordinal),
                new(cells[6], cells[7], revision, updated), SearchJudgeInput.FromRecord(record)));
        }
        return new(dataset.Path, dataset.Sha256, dataset.Records.Length, rows.ToImmutable());
    }
}
