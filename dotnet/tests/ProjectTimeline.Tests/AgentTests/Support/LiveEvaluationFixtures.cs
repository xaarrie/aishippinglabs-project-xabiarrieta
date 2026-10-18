using ProjectTimeline.BusinessLogic.Agents.AgentModels;

namespace ProjectTimeline.Tests.AgentTests.Support;

// Public-domain-style synthetic conversations only. No final model responses live here.
internal static class LiveEvaluationFixtures
{
    public static ConversationFixture Get(string id) => id switch {
        "login-clear" => Derive(id, "software-functionality", "General project notes.",
            "Login assignment", "Luis and Pablo will implement the login functionality."),
        "travel-clear" => Derive(id, "travel-detailed-conditions", "Preparativos para Lisboa.",
            "Reparto del traslado", "Ane y Luis organizarán el traslado desde el aeropuerto de Lisboa al alojamiento."),
        "business-clear" => Derive(id, "business-partial-answers", "The customer retention project is being planned.",
            "Renewal analysis", "Priya and Omar will analyze customer renewal retention."),
        "team-context" => Team(id, false),
        "team-marta" => Team(id, true),
        "software-details" => Derive(id, "software-functionality", "The project is planning a login feature.",
            "Login implementation", "Luis will implement the login UI; Pablo will implement the login API. " +
            "Use password-only authentication, no SSO. The launch might be Friday, only if Security approves it; this is not confirmed. " +
            "Lock human accounts after five failed login attempts for ten minutes. Service accounts are exempt from this lockout. " +
            "Retain audit logs for thirty days."),
        "travel-details" => ConversationFixtures.Get("travel-detailed-conditions"),
        "business-details" => Derive(id, "business-partial-answers",
            "The North pilot serves shops. Finance has not approved the expansion budget.",
            "North retention objective", "Aim to improve renewal retention from 72% to 80% in the North pilot, " +
            "measured over three complete monthly cohorts and excluding trial accounts. Do not increase discounts above 5%. " +
            "Expand the successful plan only after Finance approves the budget. " +
            "Keep the support team involved because cancellations often start with unresolved tickets."),
        "date-owner-answers" => Derive(id, "software-functionality", "The login rollout is planned, not completed.",
            "Login rollout", "Prepare the login rollout. The launch date is either 2026-10-16 or 2026-10-23; confirm which. " +
            "The reporting owner is either Luis or Pablo; confirm who owns reporting. " +
            "Use password-only authentication, no SSO. Retain audit logs for thirty days."),
        "ambiguous-export" => ConversationFixtures.Get("software-ambiguous-control"),
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown synthetic evaluation scenario.")
    };

    public const string DateAnswer = "The launch date is 2026-10-23.";
    public const string OwnerAnswer = "Pablo owns reporting; Luis is not the reporting owner.";

    // Controlled INPUT draft requested for OE-04, NOT a substituted final response.
    // Q1 is explicitly date and Q2 explicitly owner, irrespective of how a model might split questions.
    public static EntryReviewResult ControlledPreviousResult()
    {
        var task = new TaskItem(null, "Prepare login rollout", "Preserve the original rollout source.", null,
            "Planned", null, [], [], ["Q1", "Q2"]) {
            Details = ["Launch date: 2026-10-16 or 2026-10-23, unresolved.",
                "Reporting owner: Luis or Pablo, unresolved.", "Password-only authentication, no SSO.",
                "Retain audit logs for thirty days."]
        };
        return new(Status.NeedsClarification,
            [new("Q1", QuestionType.Ambiguity, Impact.Blocking, "Which launch date?", "2026-10-16 or 2026-10-23", null,
                ["2026-10-16", "2026-10-23"]),
             new("Q2", QuestionType.Ambiguity, Impact.Blocking, "Who owns reporting?", "either Luis or Pablo", null,
                ["Luis", "Pablo"])],
            new("Login rollout", "Original source: planned login rollout; date and reporting owner are unresolved.",
                [task], [], [], []), [], "Controlled input draft with two unanswered questions.");
    }

    private static ConversationFixture Team(string id, bool marta) => Derive(id, "software-functionality",
        marta
            ? "Prior plan: a team was planned to implement the login functionality. Marta was already a planned member; no work had started. No individual roles were assigned."
            : "Prior plan: a team was planned to implement the login functionality. No work had started. No individual roles were assigned.",
        "Login team assignment", "Luis and Pablo will work on the previously planned login team.");

    private static ConversationFixture Derive(string id, string templateId, string history, string title, string source)
    {
        // Reuse the established three-domain corpus boundaries, immutable records and fake tools.
        var template = ConversationFixtures.Get(templateId);
        var priorId = template.Entries.Single(entry => entry.Sequence == 20).Id;
        return template with {
            Id = id,
            Entries = template.Entries.Select(entry => entry.Id == template.CurrentEntryId
                ? entry with { Title = title, Content = source }
                : entry.Id == priorId ? entry with { Title = "Prior context", Content = history } : entry).ToArray(),
            ExpectedFacts = [new("source", template.CurrentEntryId, source)],
            ContextOnlyFacts = [new("prior-source", priorId, history)],
            ForbiddenFacts = [], Clarifications = [], PartialAnswers = null, CompleteAnswers = null
        };
    }
}
