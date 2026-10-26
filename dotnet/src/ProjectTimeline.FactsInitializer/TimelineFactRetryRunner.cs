using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.FactsInitializer;

public sealed record TimelineFactRetryOptions(Guid ActorId, Guid TimelineId, bool Apply = false, int PageSize = 50);

/// <summary>Opt-in recovery of existing due extraction retries, never a queue drain or job reset.</summary>
public sealed class TimelineFactRetryRunner(Func<ProjectTimelineDbContext> createContext,
    Func<ITimelineFactProvider> createProvider, string extractionModel)
{
    public async Task RunAsync(TimelineFactRetryOptions options, Action<EntryResult> report, CancellationToken ct = default)
    {
        if (options.ActorId == Guid.Empty || options.TimelineId == Guid.Empty || options.PageSize is < 1 or > 100
            || string.IsNullOrWhiteSpace(extractionModel)) throw new ArgumentException("Invalid retry options.");
        int upper;
        await using (var db = createContext())
        {
            await AuthorizeAsync(db, options, ct);
            upper = await db.TimelineEntries.Where(x => x.TimelineId == options.TimelineId)
                .Select(x => (int?)x.EntryNumber).MaxAsync(ct) ?? 0;
        }
        int? after = null;
        ITimelineFactProvider? provider = null;
        while (true)
        {
            List<TimelineEntry> entries;
            await using (var db = createContext())
            {
                var query = db.TimelineEntries.AsNoTracking().Where(x => x.TimelineId == options.TimelineId && x.EntryNumber <= upper);
                if (after is int cursor) query = query.Where(x => x.EntryNumber > cursor);
                entries = await query.OrderBy(x => x.EntryNumber).Take(options.PageSize).ToListAsync(ct);
            }
            if (entries.Count == 0) return;
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                string outcome;
                string? diagnostic = null;
                try { outcome = await RetryAsync(entry, options, () => provider ??= createProvider(), code => diagnostic = code, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (UnauthorizedAccessException) { outcome = "AccessDenied"; }
                catch (Exception ex) when (ex is TimelineFactConflictException or DbUpdateConcurrencyException) { outcome = "Stale"; }
                catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" or "40001" or "40P01" })
                { outcome = "Stale"; }
                catch (PostgresException ex) when (ex.SqlState is "40001" or "40P01") { outcome = "Stale"; }
                catch (Exception) { outcome = "Failed"; } // Fixed codes only; no source or exception payloads.
                report(new(entry.Id, outcome, diagnostic));
            }
            after = entries[^1].EntryNumber;
        }
    }

    private async Task<string> RetryAsync(TimelineEntry selected, TimelineFactRetryOptions options,
        Func<ITimelineFactProvider> provider, Action<string> diagnostic, CancellationToken ct)
    {
        TimelineFactJob job;
        await using (var db = createContext())
        {
            var jobs = await db.TimelineFactJobs.AsNoTracking().Where(x => x.EntryId == selected.Id && x.Kind == "Extraction"
                && x.State != "Superseded").Take(2).ToListAsync(ct);
            if (jobs.Count != 1) return "ExistingWork";
            job = jobs[0];
        }
        var owner = $"retry:{Guid.NewGuid():N}";
        var check = await CheckAsync(selected, job, options, "Retry", owner, ct);
        if (check is not null) return check;
        if (!options.Apply) return "WouldRetry";

        var actual = provider();
        if (actual.ExtractionModel != extractionModel) return "IncompatibleModel";
        // Processing acquires the existing job with its normal CAS and increments Attempts, without resetting history.
        check = await CheckAsync(selected, job, options, "Retry", owner, ct);
        if (check is not null) return check;
        string? blocked = null;
        var invoked = false;
        var guarded = new GuardedProvider(actual, async token =>
        {
            // Runs AFTER lease acquisition and immediately BEFORE the only paid operation.
            try { blocked = await CheckAsync(selected, job, options, "Running", owner, token); }
            catch (UnauthorizedAccessException) { blocked = "AccessDenied"; }
            if (blocked is not null) throw new TimelineFactProviderException("Unavailable");
            invoked = true;
        });
        ProjectTimelineDbContext ProcessingContext()
        {
            var db = createContext();
            db.SavingChanges += (_, _) =>
            {
                var claim = db.ChangeTracker.Entries<TimelineFactJob>().SingleOrDefault(x => x.Entity.Id == job.Id
                    && x.Entity.State == "Running" && x.Entity.LeaseOwner == owner);
                // The shared processor also supports Pending/expired Running work. This mode must not:
                // bind its first lease write to the exact Retry snapshot selected by this invocation.
                if (claim is not null && (claim.Property(x => x.State).OriginalValue != "Retry"
                    || claim.Property(x => x.Version).OriginalValue != job.Version))
                    throw new DbUpdateConcurrencyException("Retry selection changed.");
            };
            return db;
        }
        var state = await new TimelineFactProcessing(ProcessingContext, guarded, validationDiagnostic: diagnostic).ProcessAsync(job.Id, owner, ct);
        ct.ThrowIfCancellationRequested();
        if (blocked is not null) return blocked;
        if (state == "AwaitingClarification") return invoked ? state : "Stale";
        if (state != "Completed") return state is "NotDue" or "LostLease" ? "Stale" : "ExtractionFailed";
        if (!invoked) return "Stale"; // Another invocation's completion is never reported as our save.
        await using var completed = createContext();
        var stored = await completed.TimelineFactJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == job.Id, ct);
        if (stored is null || stored.State != "Completed" || stored.Attempts != job.Attempts + 1
            || stored.SourceFingerprint != job.SourceFingerprint || stored.LeaseOwner is not null
            || stored.LeaseExpiresAtUtc is not null) return "Stale";
        // Saved reports only current pending candidate evidence from this invocation, never effective facts.
        return await GeneratedExtractionOutcome.ReadAsync(completed, selected, job.Id, ct);
    }

    private async Task<string?> CheckAsync(TimelineEntry selected, TimelineFactJob original,
        TimelineFactRetryOptions options, string stage, string owner, CancellationToken ct)
    {
        await using var db = createContext();
        await AuthorizeAsync(db, options, ct);
        var entry = await db.TimelineEntries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == selected.Id, ct);
        if (entry is null || entry.TimelineId != options.TimelineId
            || TimelineFactEvidence.Fingerprint(entry) != TimelineFactEvidence.Fingerprint(selected)
            || TimelineFactEvidence.Fingerprint(entry) != original.SourceFingerprint) return "Stale";
        if (await db.TimelineFacts.AnyAsync(x => x.EntryId == entry.Id, ct)
            || await db.TimelineFactProposals.AnyAsync(x => x.EntryId == entry.Id && x.Status != "Superseded", ct)
            || await db.TimelineFactJobs.AnyAsync(x => x.EntryId == entry.Id && x.Id != original.Id && x.State != "Superseded", ct)) return "ExistingWork";
        var job = await db.TimelineFactJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == original.Id, ct);
        if (job is null || job.EntryId != entry.Id || job.Kind != "Extraction" || job.FactId is not null
            || !TimelineFactEvidence.ExtractionKey(job) || job.SourceFingerprint != original.SourceFingerprint) return "Stale";
        if (job.Model != "unassigned" && job.Model != extractionModel) return "IncompatibleModel";
        if (job.State != stage) return stage == "Retry" ? "ExistingWork" : "Stale";
        var now = DateTime.UtcNow;
        if (stage == "Retry")
        {
            if (job.Version != original.Version) return "Stale";
            if (job.NextAttemptAtUtc > now) return "NotDue";
            if (job.LeaseExpiresAtUtc > now) return "ActiveLease";
        }
        else
        {
            if (job.Attempts != original.Attempts + 1) return "Stale";
            if (stage == "Running" && (job.LeaseOwner != owner || !(job.LeaseExpiresAtUtc > now))) return "Stale";
        }
        return null;
    }

    private static async Task<(Guid ProjectId, bool Administrator)> AuthorizeAsync(ProjectTimelineDbContext db,
        TimelineFactRetryOptions options, CancellationToken ct)
    {
        var actor = await db.Users.AsNoTracking().Where(x => x.Id == options.ActorId && x.IsActive)
            .Select(x => new { Administrator = x.UserRole != null && x.UserRole.RoleName == "Admin" }).SingleOrDefaultAsync(ct);
        if (actor is null) throw new UnauthorizedAccessException();
        var projectId = await db.Timelines.Where(x => x.Id == options.TimelineId).Select(x => (Guid?)x.ProjectId).SingleOrDefaultAsync(ct);
        if (projectId is null || !await new ProjectAuthorization(db).CanWriteAsync(projectId.Value, options.ActorId, actor.Administrator, ct))
            throw new UnauthorizedAccessException();
        return (projectId.Value, actor.Administrator);
    }

    private sealed class GuardedProvider(ITimelineFactProvider inner, Func<CancellationToken, Task> check) : ITimelineFactProvider
    {
        public string ExtractionModel => inner.ExtractionModel;
        public async Task<IReadOnlyList<TimelineFactCandidate>> ExtractAsync(TimelineFactExtraction source, CancellationToken ct)
        {
            await check(ct);
            var result = await inner.ExtractAsync(source, ct);
            await check(ct); // Also recheck the invoking actor and eligibility before candidate persistence.
            return result;
        }
        public async Task<TimelineFactExtractionOutcome> ExtractOutcomeAsync(TimelineFactExtraction source, CancellationToken ct)
        {
            await check(ct);
            var result = await inner.ExtractOutcomeAsync(source, ct);
            await check(ct);
            return result;
        }
        public Task<float[]> EmbedAsync(string claim, CancellationToken ct) => throw new InvalidOperationException("Retry never embeds.");
    }
}
