using Microsoft.EntityFrameworkCore;
using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.BusinessLogic;

public sealed class MergeReviewService(ProjectTimelineDbContext db, ProjectAuthorization authorization) : IMergeReviewService
{
    private static readonly string[] AllowedStatuses = ["Open", "Approved", "Rejected", "Closed"];

    public async Task<MergeReviewDto> CreateAsync(MergeReviewRequest request, Guid authorId, bool administrator, CancellationToken ct)
    {
        var projectId = await db.Timelines.Where(x => x.Id == request.TimelineId).Select(x => x.ProjectId).SingleOrDefaultAsync(ct);
        if (projectId == Guid.Empty || !await authorization.CanWriteAsync(projectId, authorId, administrator, ct)) throw new UnauthorizedAccessException();
        if (string.IsNullOrWhiteSpace(request.Summary) || request.Summary.Trim().Length > 4000) throw new ArgumentException("A merge review summary is required and must be at most 4000 characters.");
        var entity = new MergeRequest { TimelineId = request.TimelineId, AuthorId = authorId, Summary = request.Summary.Trim() };
        db.MergeRequests.Add(entity); await db.SaveChangesAsync(ct); return ToDto(entity);
    }

    public async Task<IReadOnlyList<MergeReviewDto>> ListAsync(Guid timelineId, Guid userId, bool administrator, CancellationToken ct)
    {
        var projectId = await db.Timelines.Where(x => x.Id == timelineId).Select(x => x.ProjectId).SingleOrDefaultAsync(ct);
        if (projectId == Guid.Empty || !await authorization.CanReadAsync(projectId, userId, administrator, ct)) throw new UnauthorizedAccessException();
        return await db.MergeRequests.AsNoTracking().Where(x => x.TimelineId == timelineId).OrderByDescending(x => x.CreatedAtUtc).Select(x => new MergeReviewDto(x.Id, x.TimelineId, x.AuthorId, x.Summary, x.Status, x.CreatedAtUtc, x.UpdatedAtUtc)).ToListAsync(ct);
    }

    public async Task<MergeReviewDto?> UpdateStatusAsync(Guid id, string status, Guid userId, bool administrator, CancellationToken ct)
    {
        if (!AllowedStatuses.Contains(status, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Merge review status is invalid.");
        var entity = await db.MergeRequests.Include(x => x.Timeline).SingleOrDefaultAsync(x => x.Id == id, ct); if (entity is null) return null;
        if (!await authorization.CanWriteAsync(entity.Timeline.ProjectId, userId, administrator, ct)) throw new UnauthorizedAccessException();
        entity.Status = status; await db.SaveChangesAsync(ct); return ToDto(entity);
    }

    private static MergeReviewDto ToDto(MergeRequest x) => new(x.Id, x.TimelineId, x.AuthorId, x.Summary, x.Status, x.CreatedAtUtc, x.UpdatedAtUtc);
}
