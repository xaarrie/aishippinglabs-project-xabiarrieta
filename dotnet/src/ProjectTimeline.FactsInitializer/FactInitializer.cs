using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.FactsInitializer;

public sealed record InitializerOptions(Guid ActorId, bool Apply = false, int PageSize = 50);
public sealed record EntryResult(Guid EntryId, string Outcome, string? DiagnosticCode = null);

/// <summary>Explicit legacy initialization only. Never resumes existing work or drains a queue.</summary>
public sealed class FactInitializer(Func<ProjectTimelineDbContext> createContext, Func<ITimelineFactProvider> createProvider)
{
    public async Task RunAsync(InitializerOptions options, Action<EntryResult> report, CancellationToken ct = default)
    {
        if (options.ActorId == Guid.Empty || options.PageSize is < 1 or > 100)
            throw new ArgumentException("Invalid initializer options.");
        await using (var db = createContext())
            await AdministratorAsync(db, options.ActorId, ct);

        // Fixed UUID upper bound, keyset pagination; no unbounded ID list or global queue query.
        Guid through;
        await using (var db = createContext())
            through = await db.Timelines.OrderByDescending(x => x.Id).Select(x => x.Id).FirstOrDefaultAsync(ct);
        Guid? after = null;
        ITimelineFactProvider? provider = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            List<Guid> timelines;
            await using (var db = createContext())
            {
                var query = db.Timelines.AsNoTracking().Where(x => x.Id.CompareTo(through) <= 0);
                if (after is Guid cursor) query = query.Where(x => x.Id.CompareTo(cursor) > 0);
                timelines = await query.OrderBy(x => x.Id).Select(x => x.Id).Take(options.PageSize).ToListAsync(ct);
            }
            if (timelines.Count == 0) return;
            foreach (var timelineId in timelines)
            {
                int upper;
                await using (var db = createContext())
                    upper = await db.TimelineEntries.Where(x => x.TimelineId == timelineId)
                        .Select(x => (int?)x.EntryNumber).MaxAsync(ct) ?? 0;
                int? last = null;
                while (true)
                {
                    List<TimelineEntry> entries;
                    await using (var db = createContext())
                    {
                        var query = db.TimelineEntries.AsNoTracking().Where(x => x.TimelineId == timelineId
                            && x.EntryNumber <= upper && !db.TimelineFacts.Any(f => f.EntryId == x.Id));
                        if (last is int number) query = query.Where(x => x.EntryNumber > number);
                        entries = await query.OrderBy(x => x.EntryNumber).Take(options.PageSize).ToListAsync(ct);
                    }
                    if (entries.Count == 0) break;
                    foreach (var entry in entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        string outcome;
                        string? diagnostic = null;
                        try
                        {
                            outcome = await ProcessEntryAsync(entry, options,
                                () => provider ??= createProvider(), code => diagnostic = code, ct);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (UnauthorizedAccessException) { outcome = "AccessDenied"; }
                        catch (Exception ex) when (ex is TimelineFactConflictException or DbUpdateConcurrencyException)
                        { outcome = "Stale"; }
                        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" or "40001" or "40P01" })
                        { outcome = "Stale"; }
                        catch (PostgresException ex) when (ex.SqlState is "40001" or "40P01") { outcome = "Stale"; }
                        catch (Exception) { outcome = "Failed"; } // Never expose provider/DB payloads or entry text.
                        report(new(entry.Id, outcome, diagnostic));
                    }
                    last = entries[^1].EntryNumber;
                }
            }
            after = timelines[^1];
        }
    }

    private async Task<string> ProcessEntryAsync(TimelineEntry selected, InitializerOptions options,
        Func<ITimelineFactProvider> provider, Action<string> diagnostic, CancellationToken ct)
    {
        Guid jobId;
        await using (var db = createContext())
        {
            await using var tx = options.Apply && db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
            var check = await CheckAsync(db, selected, options.ActorId, null, ct);
            if (check.Outcome is not null) return check.Outcome;
            if (!options.Apply) return "WouldSave";
            var job = TimelineFactProcessing.NewExtractionJob(check.Entry!);
            db.TimelineFactJobs.Add(job);
            await db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
            jobId = job.Id;
        }

        // Recheck after reservation, before even constructing a paid provider.
        await using (var db = createContext())
        {
            var check = await CheckAsync(db, selected, options.ActorId, jobId, ct);
            if (check.Outcome is not null) return check.Outcome;
        }
        string? blocked = null;
        var guarded = new CheckedProvider(provider(), async token =>
        {
            await using var db = createContext();
            blocked = (await CheckAsync(db, selected, options.ActorId, jobId, token)).Outcome;
            if (blocked is not null) throw new TimelineFactProviderException("Unavailable");
        });
        var state = await new TimelineFactProcessing(createContext, guarded, validationDiagnostic: diagnostic).ProcessAsync(jobId, $"initializer:{jobId:N}", ct);
        ct.ThrowIfCancellationRequested();
        if (blocked is not null) return blocked;
        if (state != "Completed")
        {
            await using var db = createContext();
            var changed = await CheckAsync(db, selected, options.ActorId, jobId, ct);
            return changed.Outcome ?? (state is "LostLease" or "NotDue" ? "Stale" : "ExtractionFailed");
        }

        // Completion already atomically saved direct facts and queued embeddings. No review is prepared or accepted.
        await using var completed = createContext();
        var completedJob = await completed.TimelineFactJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == jobId
            && x.EntryId == selected.Id && x.Kind == "Extraction" && x.State == "Completed", ct);
        if (completedJob is null || completedJob.SourceFingerprint != TimelineFactEvidence.Fingerprint(selected)) return "Stale";
        return await completed.TimelineFacts.AnyAsync(x => x.ExtractionJobId == jobId, ct) ? "Saved" : "Empty";
    }

    private static async Task<(TimelineEntry? Entry, bool Administrator, string? Outcome)> CheckAsync(
        ProjectTimelineDbContext db, TimelineEntry selected, Guid actorId, Guid? ownJob, CancellationToken ct)
    {
        var administrator = await AdministratorAsync(db, actorId, ct);
        var entry = await db.TimelineEntries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == selected.Id, ct);
        if (entry is null || TimelineFactEvidence.Fingerprint(entry) != TimelineFactEvidence.Fingerprint(selected))
            return (entry, administrator, "Stale");
        var projectId = await db.Timelines.Where(x => x.Id == entry.TimelineId).Select(x => x.ProjectId).SingleAsync(ct);
        if (!await new ProjectAuthorization(db).CanWriteAsync(projectId, actorId, administrator, ct))
            throw new UnauthorizedAccessException();
        // ANY physical row disqualifies, including invalid facts. Do not use EffectiveAsync here.
        if (await db.TimelineFacts.AnyAsync(x => x.EntryId == entry.Id, ct)) return (entry, administrator, "Stale");
        var proposals = db.TimelineFactProposals.AsNoTracking().Where(x => x.EntryId == entry.Id);
        if (await proposals.AnyAsync(ct))
        {
            if (ownJob.HasValue) return (entry, administrator, "Stale");
            // Inspect only a bounded pending proposal for reporting; never answer or accept existing evidence.
            var questionsJson = await proposals.Where(x => x.Status == "Pending").OrderBy(x => x.Id)
                .Select(x => x.QuestionsJson).FirstOrDefaultAsync(ct);
            if (questionsJson is not null && JsonSerializer.Deserialize<TimelineFactQuestion[]>(questionsJson)
                is { } questions && questions.Any(x => x is null || string.IsNullOrWhiteSpace(x.Answer)))
                return (entry, administrator, "Questions");
            return (entry, administrator, "ExistingWork");
        }
        var jobs = db.TimelineFactJobs.Where(x => x.EntryId == entry.Id);
        if (ownJob is Guid ownId) jobs = jobs.Where(x => x.Id != ownId);
        if (await jobs.AnyAsync(ct)) return (entry, administrator, ownJob.HasValue ? "Stale" : "ExistingWork");
        return (entry, administrator, null);
    }

    private sealed class CheckedProvider(ITimelineFactProvider inner, Func<CancellationToken, Task> check) : ITimelineFactProvider
    {
        public string ExtractionModel => inner.ExtractionModel;
        public async Task<IReadOnlyList<TimelineFactCandidate>> ExtractAsync(TimelineFactExtraction source, CancellationToken ct)
        {
            await check(ct);
            var result = await inner.ExtractAsync(source, ct);
            await check(ct); // Recheck the invoking actor and eligibility after HTTP, before direct completion.
            return result;
        }
        public Task<float[]> EmbedAsync(string claim, CancellationToken ct) => throw new InvalidOperationException("Initializer never embeds.");
    }

    private static async Task<bool> AdministratorAsync(ProjectTimelineDbContext db, Guid actorId, CancellationToken ct)
    {
        var actor = await db.Users.AsNoTracking().Where(x => x.Id == actorId && x.IsActive)
            .Select(x => new { Administrator = x.UserRole != null && x.UserRole.RoleName == "Admin" }).SingleOrDefaultAsync(ct);
        if (actor is null) throw new UnauthorizedAccessException();
        return actor.Administrator;
    }
}
