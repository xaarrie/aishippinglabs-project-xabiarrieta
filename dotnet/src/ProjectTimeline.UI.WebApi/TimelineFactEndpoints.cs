using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.UI.WebApi;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TimelineFactBackfillRequest(int AfterEntryNumber, int ThroughEntryNumber, int PageSize = 50);

public static class TimelineFactEndpoints
{
    public static IApplicationBuilder UseTimelineFactBodyBounds(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.Request.Path.Value?.Contains("/fact-", StringComparison.Ordinal) == true)
        {
            const int limit = 65536;
            if (context.Request.ContentLength > limit) { context.Response.StatusCode = 413; return; }
            var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = limit;
            // Enforce independently of the hosting server, including unknown-length bodies.
            // Read at most limit + 1 bytes in memory; never spool request content to disk.
            var original = context.Request.Body;
            using var bounded = new MemoryStream();
            var buffer = new byte[8192];
            while (bounded.Length <= limit)
            {
                var count = await original.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit + 1 - bounded.Length)), context.RequestAborted);
                if (count == 0) break;
                bounded.Write(buffer, 0, count);
            }
            if (bounded.Length > limit) { context.Response.StatusCode = 413; return; }
            bounded.Position = 0;
            context.Request.Body = bounded;
            try { await next(); }
            finally { context.Request.Body = original; }
            return;
        }
        await next();
    });

    public static IEndpointRouteBuilder MapTimelineFacts(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/entries/{entryId:guid}/facts", (Guid entryId, ClaimsPrincipal user, TimelineFactApplication service, CancellationToken ct) =>
            RespondAsync(user, (actor, admin) => service.ReadAsync(entryId, actor, admin, ct))).RequireAuthorization();
        app.MapPost("/api/entries/{entryId:guid}/fact-proposals", (Guid entryId, TimelineFactPlanRequest request, ClaimsPrincipal user, TimelineFactApplication service, CancellationToken ct) =>
            RespondAsync(user, (actor, admin) => service.PlanAsync(entryId, actor, admin, request, ct))).RequireAuthorization();
        app.MapPost("/api/entries/{entryId:guid}/fact-proposals/{proposalId:guid}/accept", (Guid entryId, Guid proposalId, HttpRequest request, ClaimsPrincipal user, TimelineFactApplication service, CancellationToken ct) =>
            AcceptIdOnlyAsync(request, () => RespondAsync(user, (actor, admin) => service.AcceptAsync(entryId, proposalId, actor, admin, ct)), ct)).RequireAuthorization();
        app.MapPost("/api/timelines/{timelineId:guid}/fact-backfill", (Guid timelineId, TimelineFactBackfillRequest request, ClaimsPrincipal user,
            ProjectTimelineDbContext db, TimelineFactProcessing processing, CancellationToken ct) => RespondAsync(user, async (actor, admin) =>
            {
                var projectId = await db.Timelines.Where(x => x.Id == timelineId).Select(x => x.ProjectId).SingleOrDefaultAsync(ct);
                if (projectId == Guid.Empty) throw new KeyNotFoundException();
                return await processing.EnqueueAsync(projectId, timelineId, actor, admin, request.AfterEntryNumber, request.ThroughEntryNumber, request.PageSize, ct);
            })).RequireAuthorization();
        return app;
    }

    public static async Task<IResult> AcceptIdOnlyAsync(HttpRequest request, Func<Task<IResult>> accept, CancellationToken ct = default)
    {
        // Reject every supplied byte, including chunked bodies. Only the URL ID is authoritative.
        var one = new byte[1];
        if (await request.Body.ReadAsync(one.AsMemory(), ct) != 0)
            return Results.BadRequest(new { error = "Acceptance takes only a server proposal ID; no request body." });
        return await accept();
    }

    public static async Task<IResult> RespondAsync<T>(ClaimsPrincipal user, Func<Guid, bool, Task<T>> action)
    {
        if (user.Identity?.IsAuthenticated != true || !Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var actor)) return Results.Unauthorized();
        try { return Results.Ok(await action(actor, user.IsInRole("Admin"))); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (TimelineFactConflictException) { return Results.Conflict(new { error = "Fact state changed or clarification is incomplete. Refresh and regenerate the proposal." }); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "Fact state changed. Refresh and regenerate the proposal." }); }
        catch (ArgumentException) { return Results.BadRequest(new { error = "Invalid or oversized fact review request." }); }
        catch (TimelineFactProviderException) { return Results.Json(new { error = "Fact provider unavailable. Retry later; no facts were accepted." }, statusCode: 502); }
    }
}
