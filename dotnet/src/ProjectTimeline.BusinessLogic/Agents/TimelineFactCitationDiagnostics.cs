using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using ProjectTimeline.BusinessLogic.Agents.AgentModels;

namespace ProjectTimeline.BusinessLogic.Agents;

// Copied only from the two safe projections actually sent to synthesis. No evidence text survives here.
internal sealed class TimelineFactCitationCatalog
{
    internal FrozenSet<Guid> Facts { get; }
    internal FrozenSet<Guid> Contexts { get; }
    internal ImmutableArray<Guid> OmittedEntryIds { get; }
    internal bool Truncated { get; }

    private TimelineFactCitationCatalog(JsonElement matches, JsonElement sources)
    {
        Facts = matches.GetProperty("Results").EnumerateArray().SelectMany(x => x.GetProperty(nameof(Facts)).EnumerateArray())
            .Select(x => x.GetProperty("Id").GetGuid()).ToFrozenSet();
        Contexts = sources.GetProperty(nameof(Contexts)).EnumerateArray().Select(x => x.GetProperty("EvidenceId").GetGuid()).ToFrozenSet();
        OmittedEntryIds = sources.GetProperty(nameof(OmittedEntryIds)).EnumerateArray().Select(x => x.GetGuid()).Distinct().Order().ToImmutableArray();
        Truncated = matches.GetProperty(nameof(Truncated)).GetBoolean() || sources.GetProperty(nameof(Truncated)).GetBoolean();
    }

    internal static TimelineFactCitationCatalog FromProjection(JsonElement matches, JsonElement sources) => new(matches, sources);

    internal void Validate(JsonElement root, TimelineFactCitationCapture? capture)
    {
        // Both arrays must be unambiguous before an observation can claim even an empty set.
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
        var fields = root.EnumerateObject().ToArray();
        if (new[] { "citedFactIds", "citedContextEvidenceIds" }.Any(name =>
            fields.Count(x => x.Name == name) != 1 || root.GetProperty(name).ValueKind != JsonValueKind.Array)) throw new JsonException();
        var facts = Read(root.GetProperty("citedFactIds"), Facts);
        var contexts = Read(root.GetProperty("citedContextEvidenceIds"), Contexts);
        capture?.Observe(new(facts, contexts)); // Replace, never increment: SDK admission can run more than once.
        if (facts.InvalidGuidCount + contexts.InvalidGuidCount > 0 || facts.UnknownDistinctCount + contexts.UnknownDistinctCount > 0)
            throw new JsonException();
    }

    private static TimelineFactCitationSet Read(JsonElement array, FrozenSet<Guid> allowed)
    {
        var valid = new List<Guid>();
        var invalid = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && Guid.TryParseExact(item.GetString(), "D", out var id)) valid.Add(id);
            else invalid++;
        }
        return Summarize(valid, invalid, allowed);
    }

    internal static TimelineFactCitationSet Summarize(IEnumerable<Guid> ids, int invalid, FrozenSet<Guid> allowed)
    {
        var values = ids.ToArray();
        var distinct = values.Distinct().Order().ToArray();
        var unknown = distinct.Where(x => !allowed.Contains(x)).ToArray();
        return new(Clip(distinct), values.Length, distinct.Length, invalid, Clip(unknown), unknown.Length,
            distinct.Length > DiagnosticIdLimit || unknown.Length > DiagnosticIdLimit);
    }

    internal const int DiagnosticIdLimit = 128;
    internal static ImmutableArray<Guid> Clip(IEnumerable<Guid> ids) => ids.Order().Take(DiagnosticIdLimit).ToImmutableArray();
}

public sealed record TimelineFactCitationSet(ImmutableArray<Guid> CitedIds, int CitedOccurrenceCount, int CitedDistinctCount,
    int InvalidGuidCount, ImmutableArray<Guid> UnknownIds, int UnknownDistinctCount, bool DiagnosticClipped);
public sealed record TimelineFactCitationObservation(TimelineFactCitationSet Facts, TimelineFactCitationSet Contexts);
public sealed record TimelineFactCitationAvailability(ImmutableArray<Guid> AvailableFactIds, int AvailableFactCount,
    ImmutableArray<Guid> ContextEvidenceIds, int ContextEvidenceCount, ImmutableArray<Guid> OmittedEntryIds,
    int OmittedEntryCount, bool EvidenceTruncated, bool DiagnosticClipped);
public sealed record TimelineFactCitationLedger(ImmutableArray<Guid> FactIds, int FactCount,
    ImmutableArray<Guid> ContextEvidenceIds, int ContextEvidenceCount, bool EvidenceTruncated, bool DiagnosticClipped);
public sealed record TimelineFactCitationSnapshot(TimelineFactCitationAvailability? Available,
    TimelineFactCitationObservation? Observed, bool FinalValidationAttempted, TimelineFactCitationLedger? FinalLedger,
    bool PublicationAttempted, bool Published);

/// <summary>Supply a fresh holder per invocation. Snapshots contain only IDs, counts and fixed phase flags, never raw content.</summary>
public sealed class TimelineFactCitationCapture
{
    private readonly object gate = new();
    private TimelineFactCitationSnapshot snapshot = new(null, null, false, null, false, false);
    public TimelineFactCitationSnapshot Snapshot { get { lock (gate) return snapshot; } }

    internal void Available(TimelineFactCitationCatalog catalog)
    {
        lock (gate) snapshot = snapshot with { Available = new(
            TimelineFactCitationCatalog.Clip(catalog.Facts), catalog.Facts.Count,
            TimelineFactCitationCatalog.Clip(catalog.Contexts), catalog.Contexts.Count,
            TimelineFactCitationCatalog.Clip(catalog.OmittedEntryIds), catalog.OmittedEntryIds.Length, catalog.Truncated,
            catalog.Facts.Count > TimelineFactCitationCatalog.DiagnosticIdLimit || catalog.Contexts.Count > TimelineFactCitationCatalog.DiagnosticIdLimit
                || catalog.OmittedEntryIds.Length > TimelineFactCitationCatalog.DiagnosticIdLimit) };
    }

    internal void Observe(TimelineFactCitationObservation observation)
    { lock (gate) snapshot = snapshot with { Observed = observation }; }

    internal void ObserveTyped(TimelineFactInformationResult result, TimelineFactCitationCatalog catalog)
    {
        lock (gate)
        {
            if (snapshot.Observed is not null || result?.CitedFactIds is null || result.CitedContextEvidenceIds is null) return;
            snapshot = snapshot with { Observed = new(
                TimelineFactCitationCatalog.Summarize(result.CitedFactIds, 0, catalog.Facts),
                TimelineFactCitationCatalog.Summarize(result.CitedContextEvidenceIds, 0, catalog.Contexts)) };
        }
    }

    internal void AttemptFinalValidation()
    { lock (gate) snapshot = snapshot with { FinalValidationAttempted = true }; }

    internal void Validated(TimelineFactResearchEvidence ledger)
    {
        lock (gate) snapshot = snapshot with { FinalLedger = new(
            TimelineFactCitationCatalog.Clip(ledger.Facts.Select(x => x.Id)), ledger.Facts.Length,
            TimelineFactCitationCatalog.Clip(ledger.Contexts.Select(x => x.EvidenceId)), ledger.Contexts.Length, ledger.Truncated,
            ledger.Facts.Length > TimelineFactCitationCatalog.DiagnosticIdLimit || ledger.Contexts.Length > TimelineFactCitationCatalog.DiagnosticIdLimit) };
    }

    internal void AttemptPublication()
    { lock (gate) snapshot = snapshot with { PublicationAttempted = true }; }
    internal void Published()
    { lock (gate) snapshot = snapshot with { Published = true }; }
}
