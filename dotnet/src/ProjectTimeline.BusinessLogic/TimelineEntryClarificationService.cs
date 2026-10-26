using Microsoft.EntityFrameworkCore;
using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.BusinessLogic;

// Authentication supplies actorId/administrator. No client source, readiness, author or evidence flags.
// Use a clean scoped context and discard it after any failed write. No provider or lifecycle calls here.
public sealed record TimelineEntryClarificationState(Guid? GenerationId, string State,
    IReadOnlyList<TimelineEntryClarification> Current, IReadOnlySet<Guid> CarriedIds,
    IReadOnlyList<TimelineEntryClarification> History, bool Truncated, bool CanAnswer, bool CanResume);

public sealed class TimelineEntryClarificationService(ProjectTimelineDbContext db)
{
    // Current evidence comes from the current job, never from a source-hash search over history.
    public async Task<TimelineEntryClarificationState> ReadStateAsync(TimelineFactScope scope, Guid actorId,
        bool administrator, CancellationToken ct = default)
    {
        var source = await SourceAsync(scope, actorId, administrator, false, ct);
        var revision = await db.Timelines.AsNoTracking().Where(x => x.Id == scope.TimelineId).Select(x => x.FactRevision).SingleAsync(ct);
        var job = await db.TimelineFactJobs.AsNoTracking().SingleOrDefaultAsync(x => x.EntryId == source.Id
            && x.Kind == "Extraction" && x.State != "Superseded", ct);
        var current = new List<TimelineEntryClarification>();
        var carriedIds = new HashSet<Guid>();
        if (job is not null)
        {
            if (job.SourceFingerprint != TimelineFactEvidence.Fingerprint(source))
                throw new TimelineFactConflictException("Current source proof changed; refresh.");
            if (job.ExtractionInputJson is not null)
            {
                var snapshot = await db.ReadExtractionInputAsync(job, source, ct);
                carriedIds.UnionWith(snapshot.Input.Answers.Select(x => x.Id));
                current.AddRange(await db.TimelineEntryClarifications.AsNoTracking()
                    .Where(x => x.EntryId == source.Id && carriedIds.Contains(x.Id)).ToListAsync(ct));
                if (current.Count != carriedIds.Count) throw new TimelineFactConflictException("Missing carried answers.");
            }
            else if (job.EffectiveInputFingerprint is not null || job.State == "AwaitingClarification")
                throw new TimelineFactConflictException("Missing current extraction proof.");
            if (job.State == "AwaitingClarification")
            {
                var questions = await db.TimelineEntryClarifications.AsNoTracking().Where(x => x.EntryId == source.Id
                    && x.GenerationId == job.Id).OrderBy(x => x.QuestionKey).Take(TimelineEntryClarificationEvidence.MaxQuestions + 1).ToListAsync(ct);
                if (questions.Count == 0 || questions.Count > TimelineEntryClarificationEvidence.MaxQuestions
                    || questions.Any(x => x.SourceFingerprint != job.SourceFingerprint || x.Origin != "Extraction"))
                    throw new TimelineFactConflictException("Invalid current questions.");
                current.AddRange(questions);
            }
        }
        if (current.Count > TimelineEntryClarificationEvidence.MaxQuestions || current.Select(x => x.Id).Distinct().Count() != current.Count
            || current.Any(x => !TimelineEntryClarificationEvidence.Text(x.QuestionKey, 100)
                || !TimelineEntryClarificationEvidence.Text(x.Question, 2000)
                || x.Answer is null && (x.AnsweredById is not null || x.AnsweredAtUtc is not null)))
            throw new TimelineFactConflictException("Invalid current clarification evidence.");
        var actual = current.Where(x => x.Answer is not null).ToArray();
        TimelineEntryClarificationEvidence.Snapshot(source, actual.Select(x => new TimelineEntryAnswer(x.Id, x.EntryId,
            x.GenerationId, x.SourceFingerprint, x.QuestionKey, x.Question, x.Answer!, x.AnsweredById ?? Guid.Empty,
            x.AnsweredAtUtc ?? default)));
        foreach (var row in actual)
        {
            var author = row.AnsweredById!.Value;
            var active = await db.Users.AnyAsync(x => x.Id == author && x.IsActive, ct);
            var admin = await db.Users.AnyAsync(x => x.Id == author && x.IsActive && x.UserRole.RoleName == "Admin", ct);
            if (!active || !await new ProjectAuthorization(db).CanWriteAsync(scope.ProjectId, author, admin, ct))
                throw new TimelineFactConflictException("Actual answering author is no longer authorized.");
        }
        var ids = current.Select(x => x.Id).ToArray();
        var history = await db.TimelineEntryClarifications.AsNoTracking().Where(x => x.EntryId == source.Id && !ids.Contains(x.Id))
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).Take(101).ToListAsync(ct);
        var locked = await db.TimelineFacts.AnyAsync(x => x.EntryId == source.Id, ct)
            || await db.TimelineFactProposals.AnyAsync(x => x.EntryId == source.Id && x.Status == "Accepted", ct);
        var writable = !locked && await new ProjectAuthorization(db).CanWriteAsync(scope.ProjectId, actorId, administrator, ct);
        var waiting = job?.State == "AwaitingClarification";
        // No mixed source/revision/job snapshot is advertised as actionable.
        var after = await SourceAsync(scope, actorId, administrator, false, ct);
        var finalRevision = await db.Timelines.AsNoTracking().Where(x => x.Id == scope.TimelineId).Select(x => x.FactRevision).SingleAsync(ct);
        var finalJob = await db.TimelineFactJobs.AsNoTracking().SingleOrDefaultAsync(x => x.EntryId == source.Id
            && x.Kind == "Extraction" && x.State != "Superseded", ct);
        if (revision != finalRevision || TimelineFactEvidence.Fingerprint(source) != TimelineFactEvidence.Fingerprint(after)
            || job?.Id != finalJob?.Id || job?.Version != finalJob?.Version)
            throw new TimelineFactConflictException("Clarification state changed; refresh.");
        return new(job?.Id, locked ? "Reconciled" : job?.State ?? "NotGenerated",
            current.OrderBy(x => carriedIds.Contains(x.Id)).ThenBy(x => x.QuestionKey).ThenBy(x => x.Id).ToArray(), carriedIds,
            history.Take(100).ToArray(), history.Count > 100, writable && waiting,
            writable && waiting && current.All(x => x.Answer is not null));
    }

    public async Task<IReadOnlyList<TimelineEntryClarification>> ReadAsync(TimelineFactScope scope, Guid actorId,
        bool administrator, CancellationToken ct = default)
    {
        var source = await SourceAsync(scope, actorId, administrator, false, ct);
        var hash = TimelineFactEvidence.Fingerprint(source);
        var rows = await db.TimelineEntryClarifications.AsNoTracking().Where(x => x.EntryId == source.Id
            && x.SourceFingerprint == hash && db.TimelineFactJobs.Any(j => j.Id == x.GenerationId && j.State != "Superseded"))
            .OrderBy(x => x.QuestionKey).ThenBy(x => x.Id).Take(TimelineEntryClarificationEvidence.MaxQuestions + 1).ToListAsync(ct);
        if (rows.Count > TimelineEntryClarificationEvidence.MaxQuestions) throw new TimelineFactConflictException("Too many current questions.");
        return rows.AsReadOnly();
    }

    public async Task<IReadOnlyList<TimelineEntryAnswer>> AnswersAsync(TimelineFactScope scope, Guid actorId,
        bool administrator, CancellationToken ct = default)
    {
        var source = await SourceAsync(scope, actorId, administrator, false, ct);
        var rows = await ReadAsync(scope, actorId, administrator, ct);
        return TimelineEntryClarificationEvidence.Snapshot(source, rows.Where(x => x.Answer is not null).Select(x =>
            new TimelineEntryAnswer(x.Id, x.EntryId, x.GenerationId, x.SourceFingerprint, x.QuestionKey,
                x.Question, x.Answer!, x.AnsweredById!.Value, DateTime.SpecifyKind(x.AnsweredAtUtc!.Value, DateTimeKind.Utc))));
    }

    public async Task AnswerAsync(TimelineFactScope scope, Guid generationId, Guid clarificationId, Guid actorId,
        bool administrator, string answer, CancellationToken ct = default)
    {
        db.ChangeTracker.DetectChanges();
        if (db.ChangeTracker.HasChanges()) throw new TimelineFactConflictException("Use a clean clarification context.");
        await using var tx = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct) : null;
        var source = await SourceAsync(scope, actorId, administrator, true, ct);
        var row = await db.TimelineEntryClarifications.SingleOrDefaultAsync(x => x.Id == clarificationId
            && x.EntryId == source.Id && x.GenerationId == generationId, ct)
            ?? throw new KeyNotFoundException("Clarification not found in this scope/generation.");
        await db.Entry(row).ReloadAsync(ct);
        var job = await db.TimelineFactJobs.AsNoTracking().SingleAsync(x => x.Id == generationId, ct);
        if (job.EntryId != source.Id || job.State != "AwaitingClarification" || row.SourceFingerprint != TimelineFactEvidence.Fingerprint(source)
            || await db.TimelineFacts.AnyAsync(x => x.EntryId == source.Id, ct)
            || await db.TimelineFactProposals.AnyAsync(x => x.EntryId == source.Id && x.Status == "Accepted", ct))
            throw new TimelineFactConflictException("Clarification source or generation changed.");
        if (job.ExtractionInputJson is not null) await db.ReadExtractionInputAsync(job, source, ct);
        if (row.Answer is not null)
        {
            if (row.Answer == answer && row.AnsweredById == actorId) return;
            throw new TimelineFactConflictException("Actual answers are append-once.");
        }
        await db.StageEntryAnswerAsync(row.Id, actorId, answer, ct);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new TimelineFactConflictException("Concurrent clarification change; reload."); }
        if (tx is not null) await tx.CommitAsync(ct);
    }

    private async Task<TimelineEntry> SourceAsync(TimelineFactScope scope, Guid actor, bool administrator, bool write, CancellationToken ct)
    {
        var auth = new ProjectAuthorization(db);
        if (actor == Guid.Empty || !await db.Users.AnyAsync(x => x.Id == actor && x.IsActive, ct)
            || !(write ? await auth.CanWriteAsync(scope.ProjectId, actor, administrator, ct)
                : await auth.CanReadAsync(scope.ProjectId, actor, administrator, ct))) throw new UnauthorizedAccessException();
        var timeline = await db.Timelines.SingleOrDefaultAsync(x => x.Id == scope.TimelineId && x.ProjectId == scope.ProjectId, ct)
            ?? throw new KeyNotFoundException("Timeline not found in scope.");
        await db.Entry(timeline).ReloadAsync(ct); // Shared CAS baseline for source edits, questions and answers.
        return await db.TimelineEntries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == scope.EntryId && x.TimelineId == timeline.Id, ct)
            ?? throw new KeyNotFoundException("Entry not found in scope.");
    }
}
