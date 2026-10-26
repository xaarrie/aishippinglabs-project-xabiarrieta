using Microsoft.EntityFrameworkCore;

namespace ProjectTimeline.DataAccessLogic;

public static class TimelineEntryClarificationModel
{
    public static void Configure(ModelBuilder b)
    {
        var q = b.Entity<TimelineEntryClarification>();
        q.ToTable("TimelineEntryClarifications", t => t.HasCheckConstraint("CK_EntryClarification_Answer",
            "(\"Answer\" IS NULL AND \"AnsweredById\" IS NULL AND \"AnsweredAtUtc\" IS NULL) OR (\"Answer\" IS NOT NULL AND \"AnsweredById\" IS NOT NULL AND \"AnsweredAtUtc\" IS NOT NULL)"));
        q.HasOne<TimelineEntry>().WithMany().HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Restrict);
        q.HasOne<TimelineFactJob>().WithMany().HasForeignKey(x => x.GenerationId).OnDelete(DeleteBehavior.Restrict);
        q.HasOne<User>().WithMany().HasForeignKey(x => x.AnsweredById).OnDelete(DeleteBehavior.Restrict);
        q.HasIndex(x => new { x.GenerationId, x.QuestionKey }).IsUnique();
        q.Property(x => x.SourceFingerprint).IsRequired().HasMaxLength(64);
        q.Property(x => x.QuestionKey).IsRequired().HasMaxLength(100);
        q.Property(x => x.Question).IsRequired().HasMaxLength(2000);
        q.Property(x => x.Origin).IsRequired().HasMaxLength(30);
        q.Property(x => x.Answer).HasMaxLength(4000);
        q.Property(x => x.Version).IsConcurrencyToken();
    }
}

public sealed partial class ProjectTimelineDbContext
{
    public DbSet<TimelineEntryClarification> TimelineEntryClarifications => Set<TimelineEntryClarification>();
    private readonly HashSet<Guid> stagedQuestions = [];
    private readonly Dictionary<Guid, (Guid Actor, string Answer, DateTime At)> stagedAnswers = [];

    // Trusted worker seam: stage in the SAME SaveChanges as the lease-checked pause, never expose as a client API.
    // Does not save. The caller must discard this context if staging or SaveChanges fails.
    public async Task StageEntryQuestionsForLeaseAsync(Guid generationId, string leaseOwner, Guid version,
        IReadOnlyList<TimelineEntryQuestion> questions, CancellationToken ct = default)
    {
        if (questions.Count is < 1 or > TimelineEntryClarificationEvidence.MaxQuestions
            || questions.Any(x => x is null || !TimelineEntryClarificationEvidence.Text(x.Key, 100)
                || !TimelineEntryClarificationEvidence.Text(x.Text, 2000))
            || questions.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != questions.Count)
            throw new TimelineFactConflictException("Invalid extraction questions.");
        var job = await TimelineFactJobs.SingleOrDefaultAsync(x => x.Id == generationId, ct)
            ?? throw new TimelineFactConflictException("Generation missing.");
        if (job.Version != version || job.State != "Running" || job.LeaseOwner != leaseOwner)
            throw new TimelineFactConflictException("Extraction lease changed.");
        if (await TimelineEntryClarifications.AnyAsync(x => x.GenerationId == generationId, ct)
            || ChangeTracker.Entries<TimelineEntryClarification>().Any(x => x.Entity.GenerationId == generationId))
            throw new TimelineFactConflictException("Generation questions already exist.");
        foreach (var question in questions)
        {
            var row = new TimelineEntryClarification { EntryId = job.EntryId, GenerationId = job.Id,
                SourceFingerprint = job.SourceFingerprint, QuestionKey = question.Key, Question = question.Text };
            stagedQuestions.Add(row.Id); TimelineEntryClarifications.Add(row);
        }
        job.State = "AwaitingClarification"; job.LeaseOwner = null; job.LeaseExpiresAtUtc = null;
    }

    // Actor comes from authenticated server context, never a request body's author/provenance fields.
    public async Task StageEntryAnswerAsync(Guid clarificationId, Guid actorId, string answer, CancellationToken ct = default)
    {
        if (!TimelineEntryClarificationEvidence.Text(answer, 4000)) throw new TimelineFactConflictException("Invalid answer.");
        var row = await TimelineEntryClarifications.SingleAsync(x => x.Id == clarificationId, ct);
        if (row.Answer is not null)
        {
            if (row.Answer == answer && row.AnsweredById == actorId) return;
            throw new TimelineFactConflictException("Actual answers are append-once.");
        }
        var now = DateTime.UtcNow;
        stagedAnswers.Add(row.Id, (actorId, answer, now));
        row.Answer = answer; row.AnsweredById = actorId; row.AnsweredAtUtc = now;
    }

    private async Task ValidateEntryClarificationsAsync(CancellationToken ct)
    {
        foreach (var change in ChangeTracker.Entries<TimelineEntryClarification>().Where(x => x.State != EntityState.Unchanged).ToList())
        {
            var row = change.Entity;
            if (change.State == EntityState.Deleted) throw new TimelineFactConflictException("Entry evidence cannot be deleted.");
            var source = await TimelineEntries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.EntryId, ct)
                ?? throw new TimelineFactConflictException("Clarification source missing.");
            var timeline = await Timelines.FindAsync([source.TimelineId], ct)
                ?? throw new TimelineFactConflictException("Clarification timeline missing.");
            source = await TimelineEntries.AsNoTracking().SingleAsync(x => x.Id == row.EntryId, ct);
            if (source.TimelineId != timeline.Id) throw new TimelineFactConflictException("Clarification source moved.");
            var job = await TimelineFactJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.GenerationId, ct)
                ?? throw new TimelineFactConflictException("Clarification generation missing.");
            if (job.EntryId != row.EntryId || job.Kind != "Extraction" || !TimelineFactEvidence.ExtractionKey(job)
                || job.SourceFingerprint != row.SourceFingerprint || row.SourceFingerprint != TimelineFactEvidence.Fingerprint(source)
                || row.Origin != "Extraction" || job.State == "Superseded"
                || ChangeTracker.Entries<TimelineEntry>().Any(x => x.Entity.Id == row.EntryId && x.State != EntityState.Unchanged)
                || !TimelineEntryClarificationEvidence.Text(row.QuestionKey, 100) || !TimelineEntryClarificationEvidence.Text(row.Question, 2000))
                throw new TimelineFactConflictException("Clarification generation or source changed.");
            if (change.State == EntityState.Added)
            {
                var pause = ChangeTracker.Entries<TimelineFactJob>().SingleOrDefault(x => x.Entity.Id == job.Id);
                if (!stagedQuestions.Contains(row.Id) || row.Answer is not null || row.AnsweredById is not null || row.AnsweredAtUtc is not null
                    || job.State != "Running" || job.LeaseExpiresAtUtc <= DateTime.UtcNow || job.LeaseExpiresAtUtc is null
                    || job.LeaseOwner is null || job.ExtractionRevision != Entry(timeline).Property(x => x.FactRevision).OriginalValue
                    || pause is null || pause.State != EntityState.Modified || pause.Entity.State != "AwaitingClarification"
                    || pause.Entity.LeaseOwner is not null || pause.Entity.LeaseExpiresAtUtc is not null
                    || pause.Property(x => x.Version).OriginalValue != job.Version)
                    throw new TimelineFactConflictException("Questions require a current leased extraction pause.");
                var author = source.AuthorId;
                if (!await ClarificationWriterAsync(timeline.ProjectId, author, ct))
                    throw new TimelineFactConflictException("Extraction author cannot write this project.");
            }
            else
            {
                if (!stagedAnswers.TryGetValue(row.Id, out var staged) || row.AnsweredById != staged.Actor
                    || row.Answer != staged.Answer || row.AnsweredAtUtc != staged.At
                    || job.State != "AwaitingClarification" || change.Property(x => x.Answer).OriginalValue is not null
                    || row.AnsweredAtUtc is null || !TimelineEntryClarificationEvidence.Text(row.Answer, 4000)
                    || change.Properties.Any(p => p.IsModified && p.Metadata.Name is not ("Answer" or "AnsweredById" or "AnsweredAtUtc"))
                    || !await ClarificationWriterAsync(timeline.ProjectId, staged.Actor, ct))
                    throw new TimelineFactConflictException("Only an authorized append-once actual answer is permitted.");
                // Reserve generation as well as source: a concurrent lease/state revocation must win or conflict.
                var trackedJob = await TimelineFactJobs.FindAsync([job.Id], ct);
                if (Entry(trackedJob!).Property(x => x.Version).OriginalValue != job.Version)
                    throw new TimelineFactConflictException("Clarification generation changed.");
                Entry(trackedJob!).Property(x => x.Version).IsModified = true;
            }
            row.Version = Guid.NewGuid();
            timeline.FactRevision = checked(Entry(timeline).Property(x => x.FactRevision).OriginalValue + 1);
        }
        // Preserve evidence even if a caller deletes its entry outside the service.
        foreach (var entry in ChangeTracker.Entries<TimelineEntry>().Where(x => x.State == EntityState.Deleted))
            if (await TimelineEntryClarifications.AnyAsync(x => x.EntryId == entry.Entity.Id, ct))
                throw new TimelineFactConflictException("Clarification history locks source deletion.");
    }

    private async Task<bool> ClarificationWriterAsync(Guid projectId, Guid actor, CancellationToken ct) =>
        await Users.AnyAsync(u => u.Id == actor && u.IsActive, ct)
        && await Projects.AnyAsync(p => p.Id == projectId && (p.OwnerId == actor
            || p.Participants.Any(m => m.UserId == actor && (m.ProjectRoleName == "Creator" || m.ProjectRoleName == "Contributor"))
            || Users.Any(u => u.Id == actor && u.UserRole.RoleName == "Admin")), ct);
}
