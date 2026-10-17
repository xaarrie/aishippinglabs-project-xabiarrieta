using System.ClientModel;

namespace ProjectTimeline.BusinessLogic.Agents.AgentModels;

public enum EntryReviewStage
{
    Unknown, Initialization, AgentInvocation, ResultValidation,
    AuditInitialization, AuditInvocation, AuditResultValidation
}

// Retain only diagnostic facts, never the original message, stack, response or inner exception.
public sealed class EntryReviewFailure : Exception
{
    public const string Code = "entry_review_failed";
    public const string AuditCode = "entry_review_audit_failed";
    public string Stage { get; }
    public string Category { get; }
    public string ExceptionType { get; }
    public int? ProviderStatusCode { get; }

    public EntryReviewFailure(EntryReviewStage stage, Exception cause) : base("Entry review failed.")
    {
        Stage = stage switch
        {
            EntryReviewStage.Initialization => "initialization",
            EntryReviewStage.AgentInvocation => "agent_invocation",
            EntryReviewStage.ResultValidation => "result_validation",
            EntryReviewStage.AuditInitialization => "audit_initialization",
            EntryReviewStage.AuditInvocation => "audit_invocation",
            EntryReviewStage.AuditResultValidation => "audit_result_validation",
            _ => "unknown"
        };
        ExceptionType = cause.GetType().FullName ?? cause.GetType().Name;
        Category = stage switch
        {
            EntryReviewStage.Initialization or EntryReviewStage.AuditInitialization => "initialization_failed",
            EntryReviewStage.ResultValidation or EntryReviewStage.AuditResultValidation => "invalid_result",
            _ => "unknown"
        };
        // Inspect types/status only, including a bounded chain of wrapped transport failures.
        // An InvalidOperationException is not evidence of missing credentials.
        var depth = 0;
        for (Exception? current = cause, next; current is not null; current = next)
        {
            next = current.InnerException;
            if (++depth > 16) break;
            var status = current switch
            {
                ClientResultException sdk when sdk.Status is >= 100 and <= 599 => sdk.Status,
                HttpRequestException http when http.StatusCode.HasValue => (int)http.StatusCode.Value,
                _ => (int?)null
            };
            if (status.HasValue)
            {
                ProviderStatusCode = status;
                Category = status switch
                {
                    401 or 403 => "provider_authentication",
                    429 => "provider_rate_limited",
                    408 or 504 => "provider_timeout",
                    >= 500 and <= 599 => "provider_unavailable",
                    _ => "provider_http_error"
                };
                break;
            }
            if (current is HttpRequestException) Category = "provider_transport";
            else if (current is TimeoutException) Category = "provider_timeout";
            else if (current is OperationCanceledException) Category = "agent_canceled";
        }
    }
}
