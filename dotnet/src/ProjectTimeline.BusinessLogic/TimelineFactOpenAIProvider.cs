using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using System.ClientModel;

namespace ProjectTimeline.BusinessLogic;

/// <summary>Thin orchestration: every nonempty extraction incurs one independent verifier call.
/// Final literal evidence is reconstructed from the original source, never verifier-written text.</summary>
public sealed class TimelineFactOpenAIProvider(TimelineFactExtractionAgent extraction,
    TimelineFactSemanticVerifierAgent verifier, ICustomEmbeddingClient embeddings) : ITimelineFactProvider
{
    public string ExtractionModel => extraction.Model;
    public async Task<IReadOnlyList<TimelineFactCandidate>> ExtractAsync(TimelineFactExtraction source, CancellationToken ct)
    {
        var outcome = await ExtractOutcomeAsync(source, ct);
        if (outcome.Questions.Count > 0) throw new TimelineFactClarificationRequiredException(outcome);
        return outcome.Candidates;
    }
    public async Task<TimelineFactExtractionOutcome> ExtractOutcomeAsync(TimelineFactExtraction source, CancellationToken ct)
    {
        source = source.Freeze();
        var extracted = await extraction.ExtractOutcomeAsync(source, ct);
        var candidates = extracted.Candidates;
        ct.ThrowIfCancellationRequested();
        if (candidates.Count == 0) return extracted;
        var assessment = await verifier.VerifyAsync(source, candidates, ct);
        var spans = TimelineFactSourceEvidence.Create(source);
        var byIndex = assessment.Assessments.ToDictionary(x => x.CandidateIndex);
        var evidence = candidates.Select((_, index) =>
        {
            var selected = byIndex[index].EvidenceIds.Select(id => spans.Single(x => x.Id == id)).ToArray();
            return new TimelineFactCandidateEvidence(index, TimelineFactSourceEvidence.Primary(selected).Id,
                Array.AsReadOnly(selected.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray()));
        }).ToArray();
        var grounded = candidates.Select((candidate, index) => candidate with
        {
            SourceQuote = TimelineFactSourceEvidence.Reconstruct(source, spans, byIndex[index].EvidenceIds),
            SourceKind = spans.Single(x => x.Id == evidence[index].PrimarySpanId).SourceKind
        }).ToArray();
        // Keep legacy direct-entry validation untouched; answer candidates travel through reviewed C2b integration.
        TimelineFactProcessing.ValidateCandidateShape(grounded.Select(x => x with { SourceKind = "Entry" }).ToArray());
        if (source.Input?.Answers.Count is not > 0) TimelineFactProcessing.ValidateCandidates(grounded, source);
        ct.ThrowIfCancellationRequested();
        return new(Array.AsReadOnly(grounded), extracted.Questions, Array.AsReadOnly(evidence));
    }
    public Task<float[]> EmbedAsync(string claim, CancellationToken ct) => embeddings.EmbedAsync(claim, ct);
}

internal static class TimelineFactProviderCall
{
    internal static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try { return await call(timeout.Token); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new TimelineFactProviderException("Timeout"); }
        catch (TimelineFactProviderException) { throw; }
        catch (System.Text.Json.JsonException) { throw new TimelineFactProviderException("InvalidResponse", "ResponseEnvelope"); }
        catch (InvalidOperationException) { throw new TimelineFactProviderException("InvalidResponse", "ResponseEnvelope"); }
        catch (ClientResultException ex) { throw new TimelineFactProviderException(ex.Status == 429 ? "RateLimited" : "Unavailable"); }
        catch { throw new TimelineFactProviderException("Unavailable"); }
    }
}
