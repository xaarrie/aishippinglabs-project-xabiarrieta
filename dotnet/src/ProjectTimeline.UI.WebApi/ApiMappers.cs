using ProjectTimeline.BusinessLogic;
using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.UI.WebApi;

public static class ApiMappers
{
    public static UserRoleDto ToDto(UserRole x) => new(x.Id, x.RoleName, x.RoleName);
    public static UserDto ToDto(User x) => new(x.Id, x.Email, x.DisplayName, x.IsActive, x.IDUserRole, x.UserRole.RoleName, x.UserRole.RoleName);
    public static ProjectDto ToDto(Project x) => new(x.Id, x.Code, x.Name, x.Description, x.OwnerId, x.Owner.DisplayName, x.PrimaryTimelineId, x.CreatedAtUtc, x.UpdatedAtUtc);
    public static TimelineDto ToDto(Timeline x) => new(x.Id, x.ProjectId, x.Code, x.Name, x.Project.Code, x.Project.Name, x.CreatedAtUtc, x.UpdatedAtUtc);
    public static EntryDto ToDto(TimelineEntry x) => new(x.Id, x.TimelineId, x.EntryNumber, x.Title, x.Content, x.Status, x.AuthorId, x.Author.DisplayName, x.CreatedAtUtc, x.UpdatedAtUtc);
    public static EntryListDto ToListDto(TimelineEntry x) => new(x.Id, x.TimelineId, x.EntryNumber, x.Title, Preview(x.Content), x.Status, new AuthorDisplayDto(x.Author.Id, x.Author.DisplayName), x.Artifacts.Select(ToSummary).ToList(), x.CreatedAtUtc, x.UpdatedAtUtc);
    public static EntryDetailDto ToDetailDto(TimelineEntry x) => new(x.Id, x.TimelineId, x.EntryNumber, x.Title, x.Content, x.Status, new AuthorDisplayDto(x.Author.Id, x.Author.DisplayName), x.Artifacts.Select(ToSummary).ToList(), x.CreatedAtUtc, x.UpdatedAtUtc);
    public static ArtifactSummaryDto ToSummary(TimelineArtifact x) => new(x.Id, x.FileName, x.ContentType, x.Length, x.CreatedAtUtc);
    private static string Preview(string content)
    {
        var runes = content.EnumerateRunes().ToArray();
        return runes.Length <= 500 ? content : string.Concat(runes.Take(499).Select(r => r.ToString())) + "…";
    }
    public static ArtifactDto ToDto(TimelineArtifact x) => new(x.Id, x.EntryId, x.FileName, x.ContentType, x.Length, x.CreatedAtUtc);
}
