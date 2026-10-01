namespace ProjectTimeline.BusinessLogic;

public sealed record LoginRequest(string Email, string Password);
public sealed record CreateUserRequest(string Email, string DisplayName, string Password, bool IsActive = true, string[]? Roles = null);
public sealed record UpdateUserRequest(string DisplayName, string? Password, bool IsActive, string[]? Roles = null);
public sealed record CreateProjectRequest(string Code, string Name, string? Description);
public sealed record UpdateProjectRequest(string Name, string? Description);
public sealed record CreateTimelineRequest(string Code, string Name);
public sealed record UpdateTimelineRequest(string Name);
public sealed record CreateEntryRequest(string Title, string Content, string Status = "Draft");
public sealed record UpdateEntryRequest(string Title, string Content, string Status);
public sealed record UpdateProfileRequest(string DisplayName, string? Password);
public sealed record AddParticipantRequest(string Email, string ProjectRoleName);
public sealed record UserDto(Guid Id, string Email, string DisplayName, bool IsActive, IEnumerable<string> Roles);
public sealed record ProjectDto(Guid Id, string Code, string Name, string? Description, Guid OwnerId, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);
public sealed record TimelineDto(Guid Id, Guid ProjectId, string Code, string Name, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);
public sealed record EntryDto(Guid Id, Guid TimelineId, int EntryNumber, string Title, string Content, string Status, Guid AuthorId, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);
public sealed record ArtifactDto(Guid Id, Guid EntryId, string FileName, string ContentType, long Length, DateTime CreatedAtUtc);
