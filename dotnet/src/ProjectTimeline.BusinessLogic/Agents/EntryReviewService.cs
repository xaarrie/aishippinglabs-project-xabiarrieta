using ProjectTimeline.BusinessLogic.Agents.AgentModels;
using ProjectTimeline.DataAccessLogic;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using System.Text.Json;

namespace ProjectTimeline.BusinessLogic.Agents;

public interface IEntryReviewService
{
    Task<EntryReviewResult> ReviewAsync(TimelineEntry entry, EntryReviewRequest request, CancellationToken ct = default);
}

public sealed class EntryReviewService(ProjectTimelineDbContext db, ICustomAgent? agent = null) : IEntryReviewService
{
    public async Task<EntryReviewResult> ReviewAsync(TimelineEntry entry, EntryReviewRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var stage = EntryReviewStage.Initialization;
        try
        {
            var reviewer = new OptimizeEntryAgent(db, entry.TimelineId, entry.Id, agent);
            var data = JsonSerializer.Serialize(new
            {
                current_entry_id = entry.Id,
                new_entry = new { entry.Id, entry.Title, entry.Content, entry.Status, entry.EntryNumber },
                previous_result = request.PreviousResult,
                user_answers = request.UserAnswers
            });
            // ICustomAgent has no cancellation overload. Check at both boundaries; do not abandon
            // an in-flight run that may still be using this request's scoped database tools.
            ct.ThrowIfCancellationRequested();
            stage = EntryReviewStage.AgentInvocation;
            var response = await reviewer.RunAsync(data);
            ct.ThrowIfCancellationRequested();
            stage = EntryReviewStage.ResultValidation;
            return response?.Result ?? throw new InvalidOperationException("The reviewer returned no structured result.");
        }
        catch (Exception error)
        {
            // Caller cancellation takes precedence, including failures racing with cancellation.
            ct.ThrowIfCancellationRequested();
            throw new EntryReviewFailure(stage, error);
        }
    }
}
