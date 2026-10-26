using System.Security.Claims;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.AgentModels;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.UI.WebApi;

public sealed class TimelineFactInformationApplication(ProjectTimelineDbContext db, Func<ICustomAgent>? agentFactory = null,
    Microsoft.Extensions.AI.IChatClient? chatClient = null, TimelineFactResearchOptions? options = null,
    AgentTelemetryOptions? telemetryOptions = null)
{
    public async Task<TimelineFactInformationResponse> SearchAsync(Guid timelineId, Guid actorId, string question, CancellationToken ct = default)
    {
        // Current persisted role and active state, never caller/model supplied project/admin/entry-author scope.
        var actor = await db.Users.AsNoTracking().Include(x => x.UserRole)
            .SingleOrDefaultAsync(x => x.Id == actorId && x.IsActive, ct);
        if (actor is null) throw new UnauthorizedAccessException();
        var project = await db.Timelines.AsNoTracking().Where(x => x.Id == timelineId)
            .Select(x => (Guid?)x.ProjectId).SingleOrDefaultAsync(ct) ?? throw new KeyNotFoundException();
        var admin = actor.UserRole?.RoleName == "Admin";
        if (!await new ProjectAuthorization(db).CanReadAsync(project, actorId, admin, ct)) throw new UnauthorizedAccessException();
        return await new SearchEntriesAgent(db, new(project, timelineId), actorId, admin, agentFactory, options: options, chatClient: chatClient, telemetryOptions: telemetryOptions).RunAsync(question, ct);
    }
}

public static class TimelineFactInformationEndpoints
{
    public static IEndpointRouteBuilder MapTimelineFactInformation(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/timelines/{id:guid}/entry-search", async (Guid id, string? text, ClaimsPrincipal user,
            TimelineFactInformationApplication service, CancellationToken ct) =>
        {
            if (user.Identity?.IsAuthenticated != true || !Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var actor))
                return Results.Unauthorized();
            try { return Results.Ok(await service.SearchAsync(id, actor, text!, ct)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException) { return Results.BadRequest(new { code = "InvalidQuestion", error = "Provide searchable text of at most 300 characters." }); }
            catch (TimelineFactConflictException) { return Results.Conflict(new { code = "EvidenceChanged", error = "Fact state changed. Refresh and search again." }); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { code = "EvidenceChanged", error = "Fact state changed. Refresh and search again." }); }
            catch (DbException) { return Results.Conflict(new { code = "StorageUnavailable", error = "Fact search storage is unavailable; deployment may be pending." }); }
            catch (TimelineFactInformationTimeoutException) { return Results.Json(new { code = "ResearchDeadlineExceeded", error = "Fact research timed out. Retry later." }, statusCode: 504); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            { return Results.Json(new { code = "RequestCanceled", error = "Fact research canceled." }, statusCode: 499); }
            catch (TimelineFactInformationSynthesisException) { return Results.Json(new { code = "InvalidResearchAnswer", error = "Fact answer unavailable. Retry later." }, statusCode: 502); }
        }).RequireAuthorization();
        return app;
    }
}
