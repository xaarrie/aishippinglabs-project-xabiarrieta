namespace ProjectTimeline.BusinessLogic;

public sealed record LoginRequest(string Email, string Password);
public sealed record RecoveryRequest(string Email);
public sealed record RecoveryResetRequest(string Token, string NewPassword);
public sealed record CreateUserRequest(string Email, string DisplayName, string Password, Guid IDUserRole, bool IsActive = true);
public sealed record UpdateUserRequest(string DisplayName, string? Password, bool IsActive, Guid IDUserRole);
public sealed record CreateProjectRequest(string Code, string Name, string? Description);
public sealed record UpdateProjectRequest(string Name, string? Description, Guid? OwnerId = null, Guid? PrimaryTimelineId = null);
public sealed record CreateTimelineRequest(string Code, string Name);
public sealed record UpdateTimelineRequest(string Name);
public sealed record CreateEntryRequest(string Title, string Content, string Status = "Draft");
public sealed record UpdateEntryRequest(string Title, string Content, string Status, Guid? AuthorId = null);
public sealed record CreateSnapshotRequest(string? SummaryContent, IReadOnlyList<Guid> EntryIds);
public sealed record SnapshotDto(Guid Id, Guid TimelineId, int SnapshotNumber, DateTime CapturedAtUtc, string SummaryContent, IReadOnlyList<Guid> EntryIds, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);
public sealed record UpdateProfileRequest(string DisplayName, string? Password);
public sealed record AddParticipantRequest(Guid UserId, string ProjectRoleName);
public sealed record UpdateParticipantRequest(string ProjectRoleName);
public sealed record UserRoleDto(Guid Id, string Code, string Name);
public sealed record UserDto(Guid Id, string Email, string DisplayName, bool IsActive, Guid IDUserRole, string RoleCode, string RoleName);
public sealed record ProjectDto(Guid Id, string Code, string Name, string? Description, Guid OwnerId, string OwnerDisplayName, Guid? PrimaryTimelineId, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);
public sealed record TimelineDto(Guid Id, Guid ProjectId, string Code, string Name, string ProjectCode, string ProjectName, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);
public sealed record AuthorDisplayDto(Guid Id, string DisplayName);
public sealed record ArtifactSummaryDto(Guid Id, string FileName, string ContentType, long Length, DateTime CreatedAtUtc);
public sealed record EntryListDto(Guid Id, Guid TimelineId, int EntryNumber, string Title, string Preview, string Status, AuthorDisplayDto Author, IReadOnlyList<ArtifactSummaryDto> Artifacts, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);
public sealed record EntryDetailDto(Guid Id, Guid TimelineId, int EntryNumber, string Title, string Content, string Status, AuthorDisplayDto Author, IReadOnlyList<ArtifactSummaryDto> Artifacts, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);
public sealed record EntryDto(Guid Id, Guid TimelineId, int EntryNumber, string Title, string Content, string Status, Guid AuthorId, string AuthorDisplayName, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);
public sealed record ArtifactDto(Guid Id, Guid EntryId, string FileName, string ContentType, long Length, DateTime CreatedAtUtc);
public sealed record ParticipantDto(Guid Id, Guid ProjectId, Guid UserId, string Email, string DisplayName, string ProjectRoleName);
public sealed record CollaboratorUserDto(Guid Id, string Email, string DisplayName);

// Provider-neutral contracts. Implementations may be added without coupling the business layer to an AI vendor.
public sealed record RagIndexRequest(Guid ProjectId, Guid? TimelineId, string SourceVersion);
public sealed record RagIndexResult(Guid ProjectId, Guid? TimelineId, string Status, DateTime? CompletedAtUtc);
public sealed record RagQueryRequest(Guid ProjectId, string Query, int MaxResults = 10);
public sealed record RagQueryResult(Guid ProjectId, string Query, IReadOnlyList<RagQueryHit> Hits, string Status);
public sealed record RagQueryHit(string Source, string Excerpt, double Score);
public sealed record LifecycleSnapshotRequest(Guid ProjectId, Guid TimelineId, string SnapshotVersion);
public sealed record LifecycleSnapshotReadModel(Guid ProjectId, Guid TimelineId, string Status, DateTime? AnalyzedAtUtc, IReadOnlyList<string> Findings);
public sealed record IncongruityAnalysisRequest(Guid ProjectId, Guid TimelineId, string SnapshotVersion);
public sealed record IncongruityAnalysisReadModel(Guid ProjectId, Guid TimelineId, string Status, IReadOnlyList<string> Findings);
public interface IRagIndexService { Task<RagIndexResult> IndexAsync(RagIndexRequest request, CancellationToken ct); }
public interface IRagQueryService { Task<RagQueryResult> QueryAsync(RagQueryRequest request, CancellationToken ct); }
public interface ILifecycleAnalysisService { Task<LifecycleSnapshotReadModel> AnalyzeSnapshotAsync(LifecycleSnapshotRequest request, CancellationToken ct); Task<IncongruityAnalysisReadModel> AnalyzeIncongruitiesAsync(IncongruityAnalysisRequest request, CancellationToken ct); }
public sealed record MergeReviewRequest(Guid TimelineId, string Summary);
public sealed record MergeReviewStatusRequest(string Status);
public sealed record MergeReviewDto(Guid Id, Guid TimelineId, Guid AuthorId, string Summary, string Status, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);
public interface IMergeReviewService { Task<MergeReviewDto> CreateAsync(MergeReviewRequest request, Guid authorId, bool administrator, CancellationToken ct); Task<IReadOnlyList<MergeReviewDto>> ListAsync(Guid timelineId, Guid userId, bool administrator, CancellationToken ct); Task<MergeReviewDto?> UpdateStatusAsync(Guid id, string status, Guid userId, bool administrator, CancellationToken ct); }
