using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.AgentModels;
using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.UI.WebApi;

public static class EntryReviewEndpoints
{
    public static void MapEntryReview(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/entries/{id:guid}/review", async (Guid id, EntryReviewRequest request,
            ClaimsPrincipal principal, ProjectTimelineDbContext db, ProjectAuthorization authorization,
            IEntryReviewService reviewer, HttpContext httpContext, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var entry = await db.TimelineEntries.AsNoTracking().Include(x => x.Timeline)
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (entry is null) return Results.NotFound();
            if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
                return Results.Unauthorized();
            if (!await authorization.CanWriteAsync(entry.Timeline.ProjectId, userId, principal.IsInRole("Admin"), ct))
                return Results.Forbid();
            if (!IsValid(request))
                return Results.BadRequest(new { error = "Review input is invalid or too large. Use at most 50 answers, 64-character question IDs and 4000-character answers; total review input must be at most 100000 characters." });
            var source = new EntryReviewSource(entry.Id, entry.Title, entry.Content,
                EntryReviewFingerprints.Source(entry.Id, entry.Title, entry.Content));
            try
            {
                var result = await reviewer.ReviewAsync(entry, request, ct);
                // Keep the five legacy fields at the top level; source is server-owned, not model output.
                return Results.Ok(new { result.Status, result.Questions, result.Entry, result.DigestChanges, result.ChangeSummary, source });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                ct.ThrowIfCancellationRequested();
                var failure = error as EntryReviewFailure ?? new EntryReviewFailure(EntryReviewStage.Unknown, error);
                var reference = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;
                // Never pass an exception to a logger: even inner messages/stacks may contain secrets.
                loggerFactory.CreateLogger(typeof(EntryReviewEndpoints).FullName!).LogWarning(new EventId(4101, "EntryReviewFailed"),
                    "Entry review failed: {FailureCode} {FailureStage} {FailureCategory} {ExceptionType} {ProviderStatusCode} {DiagnosticReference}",
                    EntryReviewFailure.Code, failure.Stage, failure.Category, failure.ExceptionType, failure.ProviderStatusCode, reference);
                var action = failure.Category switch
                {
                    "provider_authentication" => "Ask an administrator to check the review provider credentials and permissions.",
                    "initialization_failed" => "Ask an administrator to check the review service configuration.",
                    "provider_rate_limited" => "Wait before trying review again; ask an administrator to check provider quota if this continues.",
                    "invalid_result" => "The reviewer returned no usable structured result. Try review again; contact an administrator if this continues.",
                    _ => "Try review again later; contact an administrator if this continues."
                };
                return Results.Problem(statusCode: StatusCodes.Status502BadGateway,
                    title: "Entry review could not be completed.",
                    detail: $"{action} Share diagnostic reference {reference} with the administrator.",
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = EntryReviewFailure.Code,
                        ["stage"] = failure.Stage,
                        ["category"] = failure.Category,
                        ["traceId"] = reference
                    });
            }
        }).RequireAuthorization();

        app.MapPost("/api/entries/{id:guid}/review/audit", async (Guid id, EntryReviewAuditRequest request,
            ClaimsPrincipal principal, ProjectTimelineDbContext db, ProjectAuthorization authorization,
            IEntryReviewAuditService auditor, HttpContext httpContext, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var entry = await db.TimelineEntries.AsNoTracking().Include(x => x.Timeline)
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (entry is null) return Results.NotFound();
            if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
                return Results.Unauthorized();
            if (!await authorization.CanWriteAsync(entry.Timeline.ProjectId, userId, principal.IsInRole("Admin"), ct))
                return Results.Forbid();
            if (!IsValid(request))
                return Results.BadRequest(new { error = "Audit requires a proposal title (1-200 characters), nonempty content and a 64-character lowercase SHA256 source fingerprint; total input must be at most 100000 characters." });
            var sourceFingerprint = EntryReviewFingerprints.Source(entry.Id, entry.Title, entry.Content);
            if (!string.Equals(sourceFingerprint, request.ExpectedSourceFingerprint, StringComparison.Ordinal))
                return Results.Conflict(new { code = "entry_review_source_changed", error = "The source changed. Review the current source before auditing again." });
            try
            {
                return Results.Ok(await auditor.AuditAsync(entry, request, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                ct.ThrowIfCancellationRequested();
                var failure = error as EntryReviewFailure ?? new EntryReviewFailure(EntryReviewStage.Unknown, error);
                var reference = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;
                loggerFactory.CreateLogger(typeof(EntryReviewEndpoints).FullName!).LogWarning(new EventId(4102, "EntryReviewAuditFailed"),
                    "Entry audit failed: {FailureCode} {FailureStage} {FailureCategory} {ExceptionType} {ProviderStatusCode} {DiagnosticReference}",
                    EntryReviewFailure.AuditCode, failure.Stage, failure.Category, failure.ExceptionType, failure.ProviderStatusCode, reference);
                return Results.Problem(statusCode: StatusCodes.Status502BadGateway,
                    title: "Entry audit could not be completed.",
                    detail: $"The proposal is still available. Ask an administrator to check the audit service using diagnostic reference {reference}.",
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = EntryReviewFailure.AuditCode, ["stage"] = failure.Stage,
                        ["category"] = failure.Category, ["traceId"] = reference,
                        ["coverage"] = null, ["inconclusive"] = true,
                        ["sourceFingerprint"] = sourceFingerprint,
                        ["proposalFingerprint"] = EntryReviewFingerprints.Proposal(request.ProposalTitle!, request.ProposalContent!)
                    });
            }
        }).RequireAuthorization();
    }

    private static bool IsValid(EntryReviewAuditRequest request) =>
        !string.IsNullOrWhiteSpace(request.ProposalTitle) && request.ProposalTitle.Length <= 200 &&
        !string.IsNullOrWhiteSpace(request.ProposalContent) &&
        request.ExpectedSourceFingerprint is { Length: 64 } fingerprint &&
        fingerprint.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
        JsonSerializer.Serialize(request).Length <= 100000;

    private static bool IsValid(EntryReviewRequest request)
    {
        if (request.UserAnswers is { } answers && (answers.Count > 50 || answers.Any(x =>
            string.IsNullOrWhiteSpace(x.Key) || x.Key.Length > 64 ||
            string.IsNullOrWhiteSpace(x.Value) || x.Value.Length > 4000))) return false;
        return JsonSerializer.Serialize(request).Length <= 100000;
    }
}
