namespace ProjectTimeline.BusinessLogic;
public sealed record LoginRequest(string Email, string Password);
public sealed record CreateProjectRequest(string Code, string Name, string? Description);
public sealed record CreateTimelineRequest(string Code, string Name);
public sealed record CreateEntryRequest(string Title, string Content);
public sealed record UpdateProfileRequest(string DisplayName, string? Password);
public sealed record AddParticipantRequest(string Email, string ProjectRoleName);
public sealed record UserDto(Guid Id, string Email, string DisplayName, bool IsActive, IEnumerable<string> Roles);
