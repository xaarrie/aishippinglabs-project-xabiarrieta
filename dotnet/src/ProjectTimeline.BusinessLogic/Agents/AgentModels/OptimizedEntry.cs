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
        [property: Description("The reorganized new entry, preserving every substantive source detail and user clarification, not a compressed summary. Do not promote prior-context facts to new entry claims.")]
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
        [property: Description("Readable background preserving relevant source context, including names, dates, conditions and uncertainty. Brevity must not remove facts; use AdditionalDetails for uncategorized details.")]
    string Context,
        List<TaskItem> Tasks,
        List<Decision> Decisions,
        [property: Description("Assumptions that are not confirmed facts.")]
    List<string> Assumptions,
        List<string> Risks)
    {
        [Description("Explicit source objectives, with full descriptions, specifics and success criteria. Empty when none are stated; do not invent goals.")]
        public List<EntryObjective> Objectives { get; init; } = [];

        [Description("All relevant source details not represented elsewhere, including uncategorized facts and original uncertain wording. Preserve names, dates, units, conditions and negative constraints; never discard a fact for lack of a category.")]
        public List<string> AdditionalDetails { get; init; } = [];
    }

    public record EntryObjective(
        string Title,
        [property: Description("Full source objective and purpose, preserving scope, conditions and rationale without invented specifics.")]
        string Description)
    {
        [Description("Source specifics including names, dates, quantities with units, constraints and original uncertain wording.")]
        public List<string> Details { get; init; } = [];

        [Description("All stated success criteria, with exact thresholds, units and conditions. Empty if unspecified.")]
        public List<string> AcceptanceCriteria { get; init; } = [];

        [Description("Ids of unanswered questions affecting this objective.")]
        public List<string> PendingQuestionIds { get; init; } = [];
    }

    public record TaskItem(
        [property: Description("Existing task id, or null if the task is new.")]
    string? Id,
        string Title,
        [property: Description("Full task meaning and rationale, not just a title or summary. Retain substantive specifics in this description or the detail fields.")]
        string Description,
        string? Owner,
        string? Status,
        [property: Description("Explicit, confirmed target date in ISO 8601 (yyyy-MM-dd), or null. Preserve tentative, relative or conditional date wording in Details and reference clarification questions; never invent a date.")]
    string? TargetDate,
        [property: Description("Ids of tasks this one depends on.")]
    List<string> Dependencies,
        [property: Description("All stated acceptance criteria with thresholds, units, conditions and exclusions, without compression or invention.")]
        List<string> AcceptanceCriteria,
        [property: Description("Ids of unanswered questions that affect this task.")]
    List<string> PendingQuestionIds)
    {
        [Description("All substantive task specifics not captured elsewhere: named people, dates, instructions, rationale and original uncertain wording. Empty if none.")]
        public List<string> Details { get; init; } = [];

        [Description("All stated limits, preconditions, exceptions and negative constraints. Preserve exact conditions and prohibitions; do not infer new ones.")]
        public List<string> Constraints { get; init; } = [];

        [Description("Source numeric amounts with their units, qualifiers and what they measure (for example cable length per sensor). Do not normalize away meaning or invent quantities.")]
        public List<string> Quantities { get; init; } = [];
    }

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
