using System.ClientModel;
using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.SearchFactsEvaluator;

public sealed record AgentFailureCause(string Category, string ExceptionType, int? HttpStatus = null,
    string? ProviderCode = null, string? Reason = null);

/// <summary>Allowlisted values only; never retains the exception or its arbitrary payloads.</summary>
public sealed record AgentFailureDiagnostic(string Category, string ExceptionType, int? HttpStatus = null, string? ProviderCode = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutionStage { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AgentFailureCause>? CausalChain { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentFailureCause? AgentObservation { get; init; }

    public static AgentFailureDiagnostic FromException(Exception error, TimelineFactExecutionStage? fallbackStage = null)
    {
        var chain = new List<AgentFailureCause>();
        var stage = TimelineFactFailureContext.Stage(error) ?? fallbackStage;
        try
        {
            // Only the first aggregate branch; bounded even for hostile/deep exception trees.
            for (var depth = 0; error is not null && depth < 8; depth++, error = error.InnerException!)
                chain.Add(Project(error));
        }
        catch (Exception) { /* Diagnostics must not replace the original failure. */ }
        var primary = chain.FirstOrDefault(node => node.Category != "Other") ?? new("Other", "Other");
        return new(primary.Category, primary.ExceptionType, primary.HttpStatus, primary.ProviderCode)
        {
            Reason = primary.Reason,
            ExecutionStage = stage.HasValue && Enum.IsDefined(stage.Value) ? stage.Value.ToString() : null,
            CausalChain = chain.ToArray()
        };
    }

    private static AgentFailureCause Project(Exception error) => error switch
    {
        // Domain subclasses MUST precede InvalidOperationException / OperationCanceledException.
        TimelineFactResearchException research => new("Research", "TimelineFactResearchException", Reason: ResearchReason(research.Code)),
        TimelineFactInformationSynthesisException synthesis => new("Synthesis", "TimelineFactInformationSynthesisException",
            Reason: synthesis.Reason is { } reason ? Enum.IsDefined(reason) ? reason.ToString() : "Other" : null),
        TimelineFactProviderException provider => new("StructuredResponse", "TimelineFactProviderException", Reason: provider.DiagnosticCode switch
        {
            "ResponseSchema" or "ResponseEnvelope" or "ResponseTruncated"
                or "ResponseRefused" or "ResponseLimit" => provider.DiagnosticCode,
            _ => "Other"
        }),
        TimelineFactInformationTimeoutException => new("Timeout", "TimelineFactInformationTimeoutException"),
        TimelineFactConflictException => new("Evidence", "TimelineFactConflictException"),
        AgentExecutionBudgetException budget => new("Budget", "AgentExecutionBudgetException", Reason: budget.Reason switch
        {
            AgentExecutionBudgetReason.ProviderCallLimit => "ProviderCallLimit",
            AgentExecutionBudgetReason.RequestCanceled => "RequestCanceled",
            AgentExecutionBudgetReason.DeadlineExceeded => "DeadlineExceeded",
            AgentExecutionBudgetReason.ScopeDisposed => "ScopeDisposed",
            _ => "Other"
        }),
        ScopeSafetyException scope => new("Scope", "ScopeSafetyException", Reason: scope.Failure switch
        {
            ScopeFailure.ActorUnavailable => "ActorUnavailable", ScopeFailure.ScopeUnavailable => "ScopeUnavailable",
            ScopeFailure.ProjectMismatch => "ProjectMismatch", ScopeFailure.NoEligibleFacts => "NoEligibleFacts", _ => "Other"
        }),
        ClientResultException provider => new("HttpProvider", "ClientResultException", Status(provider.Status), ReadProviderCode(provider)),
        HttpRequestException http => new(Status((int?)http.StatusCode).HasValue ? "HttpProvider" : "Transport",
            "HttpRequestException", Status((int?)http.StatusCode)),
        TaskCanceledException => new("Cancellation", "TaskCanceledException"),
        OperationCanceledException => new("Cancellation", "OperationCanceledException"),
        JsonException => new("JsonSchemaParse", "JsonException"),
        FormatException => new("JsonSchemaParse", "FormatException"),
        UnauthorizedAccessException => new("Authorization", "UnauthorizedAccessException"),
        KeyNotFoundException => new("Scope", "KeyNotFoundException"),
        DbException => new("Database", "DbException"),
        DbUpdateConcurrencyException => new("Evidence", "DbUpdateConcurrencyException"),
        ArgumentException => new("Argument", "ArgumentException"),
        InvalidOperationException => new("InvalidOperation", "InvalidOperationException"),
        NotSupportedException => new("Unsupported", "NotSupportedException"),
        _ => new("Other", "Other")
    };

    private static string ResearchReason(string code) => code switch
    {
        "InvalidQueryBatch" or "InvalidQuery" or "EvidenceChanged" or "UnknownOrInvalidEntryIds"
            or "MissingResearchPhase" or "ToolCallBudgetExhausted" or "InvalidResearchPhase"
            or "SourceContextUnavailable" or "FactRevisionChanged" or "EvidenceBudgetExhausted"
            or "AccessDenied" or "ScopeUnavailable" or "EvidenceReadFailed" => code,
        _ => "Other"
    };

    private static int? Status(int? status) => status is >= 100 and <= 599 ? status : null;

    private static string? ReadProviderCode(ClientResultException error)
    {
        try
        {
            // Only an already available SDK response is considered. Never read a stream or send a request.
            var content = error.GetRawResponse()?.Content;
            if (content is null) return null;
            var bytes = content.ToMemory();
            if (bytes.Length > 16 * 1024) return null;
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            if (!document.RootElement.TryGetProperty("error", out var provider)
                || !provider.TryGetProperty("code", out var code) || code.ValueKind == JsonValueKind.Null) return null;
            if (code.ValueKind != JsonValueKind.String) return "Other";
            return code.GetString() switch
            {
                "invalid_api_key" => "invalid_api_key",
                "insufficient_quota" => "insufficient_quota",
                "rate_limit_exceeded" => "rate_limit_exceeded",
                "model_not_found" => "model_not_found",
                _ => "Other"
            };
        }
        catch (Exception) { return null; } // Missing/disposed/unbuffered/malformed evidence stays unavailable.
    }
}
