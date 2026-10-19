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
        var candidates = await extraction.ExtractAsync(source, ct);
        ct.ThrowIfCancellationRequested();
        if (candidates.Count == 0) return candidates;
        var assessment = await verifier.VerifyAsync(source, candidates, ct);
        var spans = TimelineFactSourceEvidence.Create(source);
        var byIndex = assessment.Assessments.ToDictionary(x => x.CandidateIndex);
        var grounded = candidates.Select((candidate, index) => candidate with
        {
            SourceQuote = TimelineFactSourceEvidence.Reconstruct(source, spans, byIndex[index].EvidenceIds)
        }).ToArray();
        TimelineFactProcessing.ValidateCandidates(grounded, source);
        ct.ThrowIfCancellationRequested();
        return grounded;
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
