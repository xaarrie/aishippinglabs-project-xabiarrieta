using System;
using System.Collections.Generic;
using System.Text;

using System.ComponentModel;

namespace ProjectTimeline.BusinessLogic.Agents.AgentModels
{
    

    public enum Status { NeedsClarification, Ready }
    public enum QuestionType { Contradiction, Ambiguity, MissingData, Duplicate, UnknownReference }
    public enum Impact { Blocking, High, Medium, Low }
    public enum ChangeAction { Add, Update, Remove, MarkObsolete }

    public record EntryReviewResult(
        [property: Description("NeedsClarification if any question is open, otherwise Ready.")]
    Status Status,
        [property: Description("Open questions for the user, ordered by impact (blocking first). Empty if none.")]
    List<Question> Questions,
        [property: Description("The optimized, reorganized version of the new entry.")]
    OptimizedEntry Entry,
        [property: Description("Proposed changes to the project digest.")]
    List<DigestChange> DigestChanges,
        [property: Description("Short description of what was modified versus the original and why.")]
    string ChangeSummary);

    public record Question(
        [property: Description("Stable id such as Q1, Q2.")]
    string Id,
        QuestionType Type,
        Impact Impact,
        [property: Description("What was detected and why it needs clarification.")]
    string Description,
        [property: Description("Short quote from the new entry that triggered the question, or null.")]
    string? NewEntryQuote,
        [property: Description("Id or reference of the previous entry, task, or digest item involved, or null.")]
    string? PriorReference,
        [property: Description("Suggested answer options, or null if open-ended.")]
    List<string>? SuggestedOptions);

    public record OptimizedEntry(
        string Title,
        [property: Description("Concise background needed to understand the tasks.")]
    string Context,
        List<TaskItem> Tasks,
        List<Decision> Decisions,
        [property: Description("Assumptions that are not confirmed facts.")]
    List<string> Assumptions,
        List<string> Risks);

    public record TaskItem(
        [property: Description("Existing task id, or null if the task is new.")]
    string? Id,
        string Title,
        string Description,
        string? Owner,
        string? Status,
        [property: Description("Target date in ISO 8601 (yyyy-MM-dd), or null.")]
    string? TargetDate,
        [property: Description("Ids of tasks this one depends on.")]
    List<string> Dependencies,
        List<string> AcceptanceCriteria,
        [property: Description("Ids of unanswered questions that affect this task.")]
    List<string> PendingQuestionIds);

    public record Decision(
        string Description,
        string? Rationale,
        [property: Description("Ids of unanswered questions that affect this decision.")]
    List<string> PendingQuestionIds);

    public record DigestChange(
        ChangeAction Action,
        [property: Description("Id of the affected digest element, or null for additions.")]
    string? ElementId,
        string Description,
        [property: Description("One-line justification.")]
    string Justification);
}
