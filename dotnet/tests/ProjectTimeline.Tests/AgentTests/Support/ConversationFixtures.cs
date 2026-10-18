namespace ProjectTimeline.Tests.AgentTests.Support;

// These are source conversations and semantic expectations for later live evaluations, NOT model responses.
internal sealed record SourceFact(string Key, Guid SourceEntryId, string Text);
internal sealed record ForbiddenFact(string Text, string Reason);
internal sealed record ClarificationCase(string Id, string SourceQuote, string Issue);
internal sealed record AnswerRound(IReadOnlyDictionary<string, string> Answers, IReadOnlyList<string> UnresolvedQuestionIds);

internal sealed record ConversationFixture(
    string Id,
    string Domain,
    string Language,
    Guid TimelineId,
    Guid CurrentEntryId,
    IReadOnlyList<PersistedConversationEntry> Entries,
    IReadOnlyList<ConversationSnapshot> Snapshots,
    IReadOnlyList<SourceFact> ExpectedFacts,
    IReadOnlyList<SourceFact> ContextOnlyFacts,
    IReadOnlyList<ForbiddenFact> ForbiddenFacts,
    IReadOnlyList<string> UnspecifiedOptionalFields,
    IReadOnlyList<ClarificationCase> Clarifications,
    AnswerRound? PartialAnswers = null,
    AnswerRound? CompleteAnswers = null)
{
    public PersistedConversationEntry Current => Entries.Single(entry => entry.Id == CurrentEntryId);
    public FakeConversationRepository CreateRepository() => new(Entries, Snapshots);
}

internal static class ConversationFixtures
{
    public static IReadOnlyList<ConversationFixture> All { get; } =
        [SoftwareFunctionality(), SoftwareAmbiguity(), TravelPreparation(), BusinessObjectives()];

    public static ConversationFixture Get(string id) => All.Single(fixture => fixture.Id == id);

    private static ConversationFixture SoftwareFunctionality()
    {
        var seed = Seed(1,
            "Atlas team", "Marta is the backend maintainer; Leo owns the web client. The team uses entry-level review drafts, not automatic saves.",
            "Review an existing entry", "Add a Review action to each saved timeline entry. Open an editable draft in a modal and show clarification questions beside it. Cancel leaves the saved entry unchanged. Apply uses the existing explicit save action; do not auto-save the draft. Marta will handle the backend integration; keep Leo informed.");
        return new("software-functionality", "software-project", "en", seed.TimelineId, seed.Current.Id,
            seed.Entries, seed.Snapshots,
            [new("entry-action", seed.Current.Id, "Add a Review action to each saved timeline entry"),
             new("editable-modal", seed.Current.Id, "Open an editable draft in a modal"),
             new("questions", seed.Current.Id, "show clarification questions beside it"),
             new("cancel", seed.Current.Id, "Cancel leaves the saved entry unchanged"),
             new("explicit-save", seed.Current.Id, "do not auto-save the draft"),
             new("team-update", seed.Current.Id, "Marta will handle the backend integration; keep Leo informed")],
            [new("marta-role", seed.History.Id, "Marta is the backend maintainer"),
             new("leo-role", seed.History.Id, "Leo owns the web client")],
            [new("Marta owns the web client", "Reverses the prior team roles."),
             new("The draft is automatically saved", "Contradicts the explicit-save constraint."),
             new("Delivery on 2026-12-01", "No delivery date was provided.")],
            ["targetDate", "budget", "priority", "acceptanceCriteria"], []);
    }

    private static ConversationFixture SoftwareAmbiguity()
    {
        var seed = Seed(2,
            "Two export paths", "The project has a CSV export for monthly billing and a PDF export for signed reports. They have separate retention rules.",
            "Retire the old export", "Remove the old export after the replacement is validated. Keep existing downloaded files available; do not delete them.");
        return new("software-ambiguous-control", "software-project", "en", seed.TimelineId, seed.Current.Id,
            seed.Entries, seed.Snapshots,
            [new("validation-condition", seed.Current.Id, "after the replacement is validated"),
             new("keep-downloads", seed.Current.Id, "Keep existing downloaded files available; do not delete them")],
            [new("csv-path", seed.History.Id, "CSV export for monthly billing"),
             new("pdf-path", seed.History.Id, "PDF export for signed reports")],
            [new("Remove the CSV export", "The source does not identify which export is old."),
             new("Remove the PDF export", "The alternative is also unconfirmed.")],
            ["owner", "targetDate"],
            [new("Q1", "the old export", "Which existing export is being retired: billing CSV or signed-report PDF?")],
            CompleteAnswers: new(new Dictionary<string, string> { ["Q1"] = "The monthly billing CSV export; leave the signed-report PDF export unchanged." }, []));
    }

    private static ConversationFixture TravelPreparation()
    {
        var seed = Seed(3,
            "Preparativos del viaje", "Ane y Luis viajarán a Lisboa. Ane guarda los pasaportes y Luis coordina el transporte. El hotel todavía no está reservado.",
            "Traslado y equipaje", "Reservar un traslado para 4 personas desde el aeropuerto de Lisboa al alojamiento, solo cuando Ane confirme la dirección. Límite total: 80 €, incluidos peajes; no aceptar suplementos nocturnos. Pedir dos sillas infantiles, una para 3 años y otra para 6 años. Cada persona llevará una maleta de hasta 10 kg. La llegada sería quizá el viernes por la tarde, si se confirma el vuelo; no es una fecha cerrada. Conservar el justificante para repartir el gasto a partes iguales.");
        return new("travel-detailed-conditions", "travel-preparation", "es", seed.TimelineId, seed.Current.Id,
            seed.Entries, seed.Snapshots,
            [new("passengers", seed.Current.Id, "4 personas"),
             new("address-condition", seed.Current.Id, "solo cuando Ane confirme la dirección"),
             new("budget", seed.Current.Id, "80 €, incluidos peajes"),
             new("no-surcharge", seed.Current.Id, "no aceptar suplementos nocturnos"),
             new("child-seats", seed.Current.Id, "dos sillas infantiles, una para 3 años y otra para 6 años"),
             new("luggage", seed.Current.Id, "una maleta de hasta 10 kg"),
             new("tentative-arrival", seed.Current.Id, "quizá el viernes por la tarde, si se confirma el vuelo"),
             new("receipt", seed.Current.Id, "Conservar el justificante para repartir el gasto a partes iguales")],
            [new("passport-custody", seed.History.Id, "Ane guarda los pasaportes"),
             new("no-booked-hotel", seed.History.Id, "El hotel todavía no está reservado")],
            [new("El vuelo está confirmado", "La llegada es condicional."),
             new("El hotel está reservado", "Contradice el contexto previo."),
             new("80 € por persona", "El límite es total, no por persona.")],
            ["targetDate", "provider"], []);
    }

    private static ConversationFixture BusinessObjectives()
    {
        var seed = Seed(4,
            "Pilot choices", "The North pilot serves shops; the South pilot serves distributors. Both are active. Finance has not approved either expansion budget.",
            "Retention objective", "Aim to improve renewal retention from 72% to 80% in the selected pilot, measured over three complete monthly cohorts and excluding trial accounts. Do not increase discounts above 5%. Expand the successful plan only after Finance approves the budget. Use the selected pilot, but the note does not say North or South. The reporting owner is either Priya or Omar; confirm who owns it. Keep the support team involved because cancellations often start with unresolved tickets.");
        return new("business-partial-answers", "business-objectives", "en", seed.TimelineId, seed.Current.Id,
            seed.Entries, seed.Snapshots,
            [new("retention", seed.Current.Id, "from 72% to 80%"),
             new("measurement", seed.Current.Id, "three complete monthly cohorts and excluding trial accounts"),
             new("discount-limit", seed.Current.Id, "Do not increase discounts above 5%"),
             new("approval", seed.Current.Id, "only after Finance approves the budget"),
             new("pilot-alternatives", seed.Current.Id, "does not say North or South"),
             new("owner-alternatives", seed.Current.Id, "either Priya or Omar"),
             new("support-rationale", seed.Current.Id, "cancellations often start with unresolved tickets")],
            [new("pilot-context", seed.History.Id, "The North pilot serves shops; the South pilot serves distributors"),
             new("no-approval", seed.History.Id, "Finance has not approved either expansion budget")],
            [new("Finance approved the expansion", "Approval is a precondition, not a completed event."),
             new("80% across all customers", "Changes the pilot scope and trial-account exclusion."),
             new("Omar is confirmed as reporting owner", "Neither source nor answer rounds confirm Omar.")],
            ["targetDate", "budgetAmount"],
            [new("Q1", "does not say North or South", "Which pilot should use the objective?"),
             new("Q2", "either Priya or Omar", "Who owns reporting?")],
            new(new Dictionary<string, string> { ["Q1"] = "Use the North pilot; the South pilot is unchanged." }, ["Q2"]),
            new(new Dictionary<string, string> {
                ["Q1"] = "Use the North pilot; the South pilot is unchanged.",
                ["Q2"] = "Priya owns reporting; Omar only reviews the figures."
            }, []));
    }

    // Stable synthetic identities, deliberately shuffled storage and noncontiguous sequence numbers.
    // Every domain gets an old boundary, readable history/current, a future entry and a foreign entry.
    private static SeededConversation Seed(int group, string historyTitle, string history, string title, string content)
    {
        Guid Id(int item) => Guid.Parse($"00000000-0000-0000-{group:D4}-{item:D12}");
        var timeline = Id(1);
        var old = new PersistedConversationEntry(Id(10), timeline, 10, "Archived planning", "Superseded planning before the snapshot boundary.");
        var prior = new PersistedConversationEntry(Id(20), timeline, 20, historyTitle, history);
        var current = new PersistedConversationEntry(Id(30), timeline, 30, title, content);
        var future = new PersistedConversationEntry(Id(40), timeline, 40, "Later decision", "Future-only decision: purchase approved for 9000 EUR. Not available at review time.");
        var foreign = new PersistedConversationEntry(Id(50), Id(2), 25, "Other timeline", "Foreign-only fact: use the unrelated Orion supplier.");
        return new(timeline, prior, current, [future, current, foreign, old, prior],
            [new(timeline, [old.Id])]);
    }

    private sealed record SeededConversation(
        Guid TimelineId, PersistedConversationEntry History, PersistedConversationEntry Current,
        IReadOnlyList<PersistedConversationEntry> Entries, IReadOnlyList<ConversationSnapshot> Snapshots);
}
