using System.Data;
using Microsoft.EntityFrameworkCore;
using ProjectTimeline.DataAccessLogic;
using Pgvector;

namespace ProjectTimeline.BusinessLogic;

[method: System.Text.Json.Serialization.JsonConstructor]
public sealed record TimelineFactExtraction(string Title, string Content)
{
    // Trusted server seam only. Never bind this input from a client request.
    public Guid EntryId { get; private init; }
    public TimelineEntryExtractionInput? Input { get; private init; }
    // Value-only source identity: retained by record copies, never serialized into model context.
    private readonly Guid timelineId;
    private readonly int entryNumber;
    private readonly Guid authorId;
    public TimelineFactExtraction(TimelineEntry entry, TimelineEntryExtractionInput input) : this(entry.Title, entry.Content)
    {
        if (entry.Id == Guid.Empty || input is null || input.JobId == Guid.Empty || input.Answers is null)
            throw new TimelineFactProviderException("InvalidResponse", "InvalidEvidence");
        try
        {
            var answers = TimelineEntryClarificationEvidence.Snapshot(entry, input.Answers);
            if (input.SourceFingerprint != TimelineFactEvidence.Fingerprint(entry)
                || input.EffectiveFingerprint != TimelineEntryClarificationEvidence.EffectiveFingerprint(entry, answers))
                throw new TimelineFactConflictException("Input identity changed.");
            EntryId = entry.Id;
            timelineId = entry.TimelineId;
            entryNumber = entry.EntryNumber;
            authorId = entry.AuthorId;
            Input = input with { Answers = answers };
        }
        catch (TimelineFactConflictException) { throw new TimelineFactProviderException("InvalidResponse", "InvalidEvidence"); }
    }
    public TimelineFactExtraction Freeze() => Input is null ? this : new(new TimelineEntry
        { Id = EntryId, TimelineId = timelineId, EntryNumber = entryNumber, AuthorId = authorId,
            Title = Title, Content = Content }, Input);
}

// C2b must consume this outcome, not the legacy list API. Citations retain all supporting identities;
// PrimarySpanId deterministically selects the literal SourceQuote/SourceKind used on the candidate.
public sealed record TimelineFactCandidateEvidence(int CandidateIndex, string PrimarySpanId,
    IReadOnlyList<Agents.TimelineFactSourceSpan> Spans);
public sealed record TimelineFactExtractionOutcome(IReadOnlyList<TimelineFactCandidate> Candidates,
    IReadOnlyList<TimelineEntryQuestion> Questions, IReadOnlyList<TimelineFactCandidateEvidence> Evidence);
public sealed class TimelineFactClarificationRequiredException(TimelineFactExtractionOutcome outcome)
    : Exception("Extraction requires clarification; consume ExtractOutcomeAsync.")
{
    public TimelineFactExtractionOutcome Outcome { get; } = outcome;
}
public interface ITimelineFactProvider
{
    string ExtractionModel { get; }
    Task<IReadOnlyList<TimelineFactCandidate>> ExtractAsync(TimelineFactExtraction source, CancellationToken ct);
    Task<float[]> EmbedAsync(string claim, CancellationToken ct);
    async Task<TimelineFactExtractionOutcome> ExtractOutcomeAsync(TimelineFactExtraction source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        source = source.Freeze();
        if (source.Input?.Answers.Count > 0)
            throw new TimelineFactProviderException("InvalidResponse", "ClarificationInputUnsupported");
        return new(await ExtractAsync(source, ct), [], []);
    }
}
public sealed class TimelineFactProviderException : Exception
{
    public string Code { get; }
    public string? DiagnosticCode { get; }
    public TimelineFactProviderException(string code, string? diagnosticCode = null) : base("Fact provider failed.")
    {
        Code = code is "Timeout" or "RateLimited" or "InvalidResponse" or "Unavailable" ? code : "Unavailable";
        // Closed vocabulary only: never attach a provider message, source fragment, entity key or inner exception.
        DiagnosticCode = diagnosticCode is "InputLimit" or "ResponseLimit" or "ResponseEnvelope" or "ResponseSchema"
            or "ResponseTruncated" or "ResponseRefused" or "CandidateLimit" or "CandidateShape"
            or "UnsupportedQuote" or "DuplicateCandidate" or "SemanticUnsupported" or "MalformedAssessment"
            or "InvalidEvidence" or "ClarificationInputUnsupported" or "InvalidQuestions" ? diagnosticCode : null;
        if (DiagnosticCode is not null) Data["DiagnosticCode"] = DiagnosticCode;
    }
}
public sealed record TimelineFactBackfillPage(int LastEntryNumber, int Examined, int Enqueued);

/// <summary>
/// T3 supplies a fresh context factory and provider, then calls ProcessAsync for explicit durable job IDs.
/// No hosted loop, legacy scan or remote call occurs on construction. Each call owns its contexts.
/// Expired Running leases are reclaimable; cancellation releases the lease when possible, otherwise expiry recovers it.
/// </summary>
public sealed class TimelineFactProcessing(Func<ProjectTimelineDbContext> createContext, ITimelineFactProvider provider,
    TimeProvider? clock = null, Action<string>? validationDiagnostic = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private DateTime Now => time.GetUtcNow().UtcDateTime;
    public static TimelineFactJob NewExtractionJob(TimelineEntry entry) => new()
    {
        EntryId = entry.Id, Kind = "Extraction", WorkKey = $"extraction:{entry.Id:N}",
        SourceFingerprint = TimelineFactEvidence.Fingerprint(entry), Model = "unassigned"
    };

    // Explicit bounded, resumable legacy enqueue only. Caller persists the returned cursor.
    // Bounds and authorization are checked on every page; newly saved entries already have work.
    public async Task<TimelineFactBackfillPage> EnqueueAsync(Guid projectId, Guid timelineId, Guid actorId,
        bool administrator, int afterEntryNumber, int throughEntryNumber, int pageSize, CancellationToken ct = default)
    {
        if (pageSize is < 1 or > 100 || afterEntryNumber < 0 || throughEntryNumber < afterEntryNumber)
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        await using var db = createContext();
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        if (!await new ProjectAuthorization(db).CanWriteAsync(projectId, actorId, administrator, ct)
            || !await db.Users.AnyAsync(x => x.Id == actorId && x.IsActive, ct)) throw new UnauthorizedAccessException();
        if (!await db.Timelines.AnyAsync(x => x.Id == timelineId && x.ProjectId == projectId, ct)) throw new KeyNotFoundException();
        var entries = await db.TimelineEntries.Where(x => x.TimelineId == timelineId && x.EntryNumber > afterEntryNumber
            && x.EntryNumber <= throughEntryNumber).OrderBy(x => x.EntryNumber).Take(pageSize).ToListAsync(ct);
        var count = 0;
        foreach (var entry in entries)
        {
            var key = $"extraction:{entry.Id:N}";
            // Existing manually prepared evidence must not be silently replaced by backfill.
            if (await db.TimelineFactJobs.AnyAsync(x => x.WorkKey == key, ct)
                || await db.TimelineFactProposals.AnyAsync(x => x.EntryId == entry.Id, ct)) continue;
            db.TimelineFactJobs.Add(NewExtractionJob(entry)); count++;
        }
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return new(entries.LastOrDefault()?.EntryNumber ?? throughEntryNumber, entries.Count, count);
    }

    // Explicit request path is independent of the hosted poller. A regeneration must name the
    // observed generation so concurrent clicks cannot silently supersede a newer draft.
    public async Task<Guid> RequestGenerationAsync(TimelineFactScope scope, Guid actorId, bool administrator,
        bool regenerate = false, Guid? expectedGenerationId = null, CancellationToken ct = default)
    {
        await using var db = createContext();
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        if (!await db.Users.AnyAsync(x => x.Id == actorId && x.IsActive, ct)
            || !await new ProjectAuthorization(db).CanWriteAsync(scope.ProjectId, actorId, administrator, ct)) throw new UnauthorizedAccessException();
        var entry = await db.TimelineEntries.Include(x => x.Timeline).SingleOrDefaultAsync(x => x.Id == scope.EntryId && x.TimelineId == scope.TimelineId
            && x.Timeline.ProjectId == scope.ProjectId, ct) ?? throw new KeyNotFoundException();
        if (await db.TimelineFacts.AnyAsync(x => x.EntryId == entry.Id, ct)
            || await db.TimelineFactProposals.AnyAsync(x => x.EntryId == entry.Id && x.Status == "Accepted", ct))
            throw new TimelineFactConflictException("Accepted reconciliation locks generation; create a new entry for corrections.");
        var current = await db.TimelineFactJobs.SingleOrDefaultAsync(x => x.EntryId == entry.Id && x.Kind == "Extraction" && x.State != "Superseded", ct);
        if (!regenerate && current is not null) return current.Id;
        if (regenerate && (expectedGenerationId is null || !await db.TimelineFactJobs.AnyAsync(x => x.Id == expectedGenerationId
            && x.EntryId == entry.Id && x.Kind == "Extraction", ct) || current is not null && current.Id != expectedGenerationId))
            throw new TimelineFactConflictException("Generation changed; refresh before regenerating.");
        var job = NewExtractionJob(entry);
        await db.StageExtractionInputAsync(job, entry, current?.Id, ct);
        await db.SupersedeFactProposalsAsync(entry.Id, includeGeneration: true, ct);
        job.WorkKey = $"extraction:{entry.Id:N}:{job.Id:N}";
        db.TimelineFactJobs.Add(job);
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return job.Id;
    }

    /// <returns>Completed, Retry, Failed, Skipped, Superseded, Cancelled, LostLease or NotDue.</returns>
    public async Task<string> ProcessAsync(Guid jobId, string owner, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 200) throw new ArgumentException("Invalid worker identity.");
        Guid version;
        Exception? acquisitionFailure = null;
        await using (var db = createContext())
        {
            var job = await db.TimelineFactJobs.SingleOrDefaultAsync(x => x.Id == jobId, ct);
            if (job is null) return "NotDue";
            if (job.State is "Completed" or "Skipped" or "Failed" or "Superseded" or "AwaitingClarification") return job.State;
            if (job.NextAttemptAtUtc > Now || job.State == "Running" && job.LeaseExpiresAtUtc > Now) return "NotDue";
            if (job.Kind == "Extraction")
            {
                var entry = await db.TimelineEntries.AsNoTracking().SingleAsync(x => x.Id == job.EntryId, ct);
                job.ExtractionRevision = await db.Timelines.Where(x => x.Id == entry.TimelineId).Select(x => x.FactRevision).SingleAsync(ct);
                try
                {
                    if (job.ExtractionInputJson is null) await db.StageExtractionInputAsync(job, entry, ct: ct);
                    await db.ReadExtractionInputAsync(job, entry, ct);
                }
                catch (TimelineFactConflictException) { acquisitionFailure = new TimelineFactProviderException("InvalidResponse", "InvalidEvidence"); }
                if (job.Model == "unassigned")
                {
                    try { job.Model = provider.ExtractionModel; }
                    catch (Exception ex) { acquisitionFailure = ex; }
                }
            }
            job.State = "Running"; job.LeaseOwner = owner; job.LeaseExpiresAtUtc = Now.AddMinutes(3);
            job.Attempts = checked(job.Attempts + 1);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return "LostLease"; }
            version = job.Version;
        }
        try
        {
            if (acquisitionFailure is not null) throw new TimelineFactProviderException("Unavailable");
            // Context is disposed BEFORE the remote call; never hold a DB transaction over HTTP.
            TimelineFactExtraction? source = null;
            string? claim = null;
            string kind;
            await using (var db = createContext())
            {
                var job = await db.TimelineFactJobs.AsNoTracking().SingleAsync(x => x.Id == jobId, ct);
                kind = job.Kind;
                var entry = await db.TimelineEntries.AsNoTracking().SingleAsync(x => x.Id == job.EntryId, ct);
                if (TimelineFactEvidence.Fingerprint(entry) != job.SourceFingerprint) throw new TimelineFactProviderException("InvalidResponse");
                if (kind == "Extraction")
                {
                    if (job.Model != provider.ExtractionModel) throw new TimelineFactProviderException("Unavailable");
                    if (job.FactId is not null || !TimelineFactEvidence.ExtractionKey(job)
                        || await db.TimelineFacts.AnyAsync(x => x.EntryId == entry.Id, ct)
                        || await db.TimelineFactProposals.AnyAsync(x => x.EntryId == entry.Id && x.Status != "Superseded", ct)
                        || await db.TimelineFactJobs.AnyAsync(x => x.EntryId == entry.Id && x.Id != job.Id && x.State != "Superseded", ct))
                        throw new TimelineFactProviderException("InvalidResponse");
                    var projectId = await db.Timelines.Where(x => x.Id == entry.TimelineId).Select(x => x.ProjectId).SingleAsync(ct);
                    var admin = await db.Users.AnyAsync(x => x.Id == entry.AuthorId && x.IsActive && x.UserRole.RoleName == "Admin", ct);
                    if (!await db.Users.AnyAsync(x => x.Id == entry.AuthorId && x.IsActive, ct)
                        || !await new ProjectAuthorization(db).CanWriteAsync(projectId, entry.AuthorId, admin, ct))
                        throw new TimelineFactProviderException("Unavailable");
                    source = new(entry, (await db.ReadExtractionInputAsync(job, entry, ct)).Input);
                }
                else if (kind == "Embedding") claim = await EligibleClaimAsync(db, job, ct);
                else throw new TimelineFactProviderException("InvalidResponse");
            }
            TimelineFactExtractionOutcome? outcome = null;
            float[]? vector = null;
            if (source is not null)
            {
                outcome = await provider.ExtractOutcomeAsync(source, ct);
                ValidateOutcome(outcome, source);
            }
            else if (claim is not null)
            {
                vector = await provider.EmbedAsync(claim, ct);
                ValidateVector(vector);
            }
            ct.ThrowIfCancellationRequested();
            await using var completion = createContext();
            await using var tx = completion.Database.IsRelational()
                ? await completion.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
            var leased = await completion.TimelineFactJobs.SingleAsync(x => x.Id == jobId, ct);
            if (!Owns(leased, owner, version)) return "LostLease";
            if (kind == "Extraction")
            {
                var entry = await completion.TimelineEntries.AsNoTracking().SingleAsync(x => x.Id == leased.EntryId, ct);
                await completion.ReadExtractionInputAsync(leased, entry, ct);
                if (outcome!.Questions.Count > 0)
                {
                    // Generation-qualified stable keys avoid ambiguity when valid rounds reuse provider keys.
                    var questions = outcome.Questions.Select(x => new TimelineEntryQuestion(Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{leased.Id:N}:{x.Key}"))), x.Text)).ToArray();
                    await completion.StageEntryQuestionsForLeaseAsync(leased.Id, owner, version, questions, ct);
                    await completion.SaveChangesAsync(ct);
                }
                else await new TimelineFactService(completion).CompleteGeneratedExtractionAsync(leased, outcome, ct);
            }
            else
            {
                if (kind == "Embedding")
                {
                    var eligible = await EligibleClaimAsync(completion, leased, ct);
                    if (eligible is not null && eligible == claim)
                    {
                        var fact = await completion.TimelineFacts.SingleAsync(x => x.Id == leased.FactId, ct);
                        fact.Embedding = new Vector(vector!); fact.EmbeddingModel = "text-embedding-3-small"; fact.EmbeddedAtUtc = Now;
                    }
                    else leased.State = "Skipped";
                }
                if (leased.State != "Skipped") leased.State = "Completed";
                leased.LeaseOwner = null; leased.LeaseExpiresAtUtc = null; leased.LastErrorCode = null;
                await completion.SaveChangesAsync(ct);
            }
            if (tx is not null) await tx.CommitAsync(ct);
            return leased.State;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var state = await RecordFailureAsync(jobId, owner, version, "Cancelled");
            return state == "LostLease" ? state : "Cancelled";
        }
        catch (DbUpdateConcurrencyException) { return "LostLease"; }
        catch (TimelineFactConflictException)
        { return await RecordFailureAsync(jobId, owner, version, "InvalidResponse"); }
        catch (Exception ex)
        {
            // Do not propagate or log provider exception messages/payloads, including unexpected SDK errors.
            var failure = ex as TimelineFactProviderException;
            var state = await RecordFailureAsync(jobId, owner, version, failure?.Code ?? "Unavailable");
            if (failure?.DiagnosticCode is { } diagnostic) validationDiagnostic?.Invoke(diagnostic);
            return state;
        }
    }

    private static void ValidateOutcome(TimelineFactExtractionOutcome outcome, TimelineFactExtraction source)
    {
        if (outcome is null || outcome.Questions is null || outcome.Evidence is null || outcome.Candidates is null
            || outcome.Questions.Count + source.Input!.Answers.Count > 20 || outcome.Questions.Any(x => x is null
                || !TimelineEntryClarificationEvidence.Text(x.Key, 100) || !TimelineEntryClarificationEvidence.Text(x.Text, 2000)
                || !x.Text.TrimEnd().EndsWith('?'))
            || outcome.Questions.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != outcome.Questions.Count)
            throw new TimelineFactProviderException("InvalidResponse", "InvalidQuestions");
        var candidates = outcome.Candidates;
        if (source.Input!.Answers.Count == 0) ValidateCandidates(candidates, source);
        else
        {
            if (candidates.Any(x => x is null || x.SourceKind is not ("Entry" or "Clarification")))
                throw new TimelineFactProviderException("InvalidResponse", "CandidateShape");
            ValidateCandidateShape(candidates.Select(x => x with { SourceKind = "Entry" }).ToArray());
        }
        Agents.TimelineFactStructuredResponse.ValidateSource(source);
        try
        {
            var entry = new TimelineEntry { Id = source.EntryId, Title = source.Title, Content = source.Content };
            TimelineExtractionEvidence.Validate(entry, source.Input!, candidates.Select(x => new TimelineFact
                { Claim = x.Claim, EntityKey = x.EntityKey, EntityType = x.EntityType, SourceQuote = x.SourceQuote, SourceKind = x.SourceKind }).ToArray(),
                outcome.Evidence.Select(x => x is null || x.Spans is null ? null! : new TimelineExtractionSupport(x.CandidateIndex,
                    x.PrimarySpanId, x.Spans.Select(s => s is null ? null! : Agents.TimelineFactSourceEvidence.ToStored(s)).ToArray())).ToArray());
        }
        catch (TimelineFactConflictException) { throw new TimelineFactProviderException("InvalidResponse", "InvalidEvidence"); }
    }

    private bool Owns(TimelineFactJob job, string owner, Guid version) => job.State == "Running"
        && job.LeaseOwner == owner && job.Version == version && job.LeaseExpiresAtUtc > Now;

    private async Task<string> RecordFailureAsync(Guid id, string owner, Guid version, string code)
    {
        await using var db = createContext();
        var job = await db.TimelineFactJobs.SingleAsync(x => x.Id == id);
        if (!Owns(job, owner, version)) return "LostLease";
        job.State = job.Attempts >= 8 && code != "Cancelled" ? "Failed" : "Retry";
        job.LastErrorCode = code; job.LeaseOwner = null; job.LeaseExpiresAtUtc = null;
        job.NextAttemptAtUtc = Now.AddSeconds(Math.Min(3600, 5 * Math.Pow(2, Math.Min(job.Attempts, 10))));
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return "LostLease"; }
        return job.State;
    }

    private static async Task<string?> EligibleClaimAsync(ProjectTimelineDbContext db, TimelineFactJob job, CancellationToken ct)
    {
        var fact = await db.TimelineFacts.SingleOrDefaultAsync(x => x.Id == job.FactId && x.EntryId == job.EntryId && x.Valid, ct);
        if (fact is null || fact.SourceFingerprint != job.SourceFingerprint) return null;
        var entry = await db.TimelineEntries.AsNoTracking().SingleAsync(x => x.Id == fact.EntryId, ct);
        if (!await TimelineFactEvidence.Effective(db, entry.TimelineId).AnyAsync(x => x.Id == fact.Id, ct)) return null;
        return fact.Claim;
    }
    public static void ValidateVector(float[] vector)
    {
        if (vector is null || vector.Length != 1536 || vector.Any(x => !float.IsFinite(x)) || !vector.Any(x => x != 0))
            throw new TimelineFactProviderException("InvalidResponse");
    }
    internal static void ValidateCandidates(IReadOnlyList<TimelineFactCandidate> candidates, TimelineFactExtraction source)
    {
        ValidateCandidateShape(candidates);
        if (candidates.Any(x => !(source.Title.Contains(x.SourceQuote, StringComparison.Ordinal)
            || source.Content.Contains(x.SourceQuote, StringComparison.Ordinal))))
            throw new TimelineFactProviderException("InvalidResponse", "UnsupportedQuote");
    }

    // Initial model citations may be approximate. Only the final source-derived result passes ValidateCandidates.
    internal static void ValidateCandidateShape(IReadOnlyList<TimelineFactCandidate> candidates)
    {
        static bool Text(string? text, int max) => !string.IsNullOrWhiteSpace(text) && text.Length <= max;
        if (candidates is null) throw new TimelineFactProviderException("InvalidResponse", "CandidateShape");
        if (candidates.Count > 200) throw new TimelineFactProviderException("InvalidResponse", "CandidateLimit");
        if (candidates.Any(x => x is null || !Text(x.Claim, 4000) || !Text(x.EntityKey, 300)
            || !Text(x.EntityType, 100) || !Text(x.SourceQuote, 20000) || x.SourceKind != "Entry"))
            throw new TimelineFactProviderException("InvalidResponse", "CandidateShape");
        if (candidates.Distinct().Count() != candidates.Count)
            throw new TimelineFactProviderException("InvalidResponse", "DuplicateCandidate");
    }
}
