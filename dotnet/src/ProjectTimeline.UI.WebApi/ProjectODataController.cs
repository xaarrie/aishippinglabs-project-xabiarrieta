using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Routing.Controllers;
using Microsoft.EntityFrameworkCore;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.UI.WebApi;

[Authorize]
public sealed class ProjectsController(ProjectTimelineDbContext db) : ODataController
{
    [EnableQuery(PageSize = 100, AllowedQueryOptions = AllowedQueryOptions.Select | AllowedQueryOptions.Filter | AllowedQueryOptions.OrderBy | AllowedQueryOptions.Top | AllowedQueryOptions.Skip | AllowedQueryOptions.Count)]
    public IQueryable<ProjectDto> Get()
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var query = User.IsInRole("Admin") ? db.Projects.AsNoTracking() : db.Projects.AsNoTracking().Where(x => x.OwnerId == userId || x.Participants.Any(y => y.UserId == userId));
        return query.Select(x => new ProjectDto(x.Id, x.Code, x.Name, x.Description, x.OwnerId, x.Owner.DisplayName, x.PrimaryTimelineId, x.CreatedAtUtc, x.UpdatedAtUtc));
    }
}
