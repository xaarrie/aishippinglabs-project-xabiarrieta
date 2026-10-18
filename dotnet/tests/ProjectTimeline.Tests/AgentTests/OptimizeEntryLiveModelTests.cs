using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using OpenAI;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.AgentModels;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.BusinessLogic.Agents.Tracking;
using ProjectTimeline.Tests.AgentTests.Support;
using Xunit;
using Xunit.Abstractions;

namespace ProjectTimeline.Tests.AgentTests;

// Paid integration evaluations, not scripted model-response tests: only persisted context is fake.
// LiveModelFact/Theory skip unless PROJECT_TIMELINE_RUN_LIVE_AGENT_TESTS is exactly "1";
// Category=LiveModel also permits exclusion from ordinary runs. The nonparallel collection
// prevents overlap with tests that temporarily alter process-wide credential configuration.
// Every scenario uses synthetic source conversations and the production optimizer/harness.
//
// Oracle scope: Ready means Status.Ready, no Questions and no pending question IDs on tasks,
// objectives or decisions. OpenQuestions means NeedsClarification, nonempty unique Q1-style
// IDs, and bidirectional coverage between those IDs and pending references on those entities.
// These are schema invariants, not proof that all natural-language ambiguities were found.
// Lexical helpers remove accents and match case-insensitively with one-second regex timeouts.
// They search Entry fragments (title/context, individual details/assumptions/risks, joined
// fields of each task or objective, and each decision's description/rationale), not arbitrary
// response JSON, question text or evaluation metadata. A rule's patterns must all match one
// fragment; Near checks either order within a bounded character distance (normally 100).
// These bounded lexical heuristics are NOT independent semantic grades: paraphrases may fail,
// and nearby words may pass without expressing the right meaning. Inspect actual responses.
[Collection(IsolatedAgentCollection.Name)]
[Trait("Category", "LiveModel")]
public sealed class OptimizeEntryLiveModelTests(ITestOutputHelper output)
{
    private const string Model = "gpt-6-luna"; // OptimizeEntryAgent's production identity, not a test replacement.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) {
        Converters = { new JsonStringEnumConverter() }, WriteIndented = true
    };

    [LiveModelTheory]
    [InlineData("login-clear")]
    [InlineData("travel-clear")]
    [InlineData("business-clear")]
    public async Task OE_01_clear_assignments_are_ready_without_optional_questions_or_inventions(string scenario)
    {
        // The three sources already name an unambiguous subject and pair of people:
        // login implementation by Luis/Pablo; Lisbon airport-to-lodging transport by Ane/Luis;
        // renewal-retention analysis by Priya/Omar. Their derived histories are general notes,
        // not competing assignments. Missing optional planning detail must not block readiness.
        var run = CreateRun("OE-01", scenario);
        var result = await run.Review();
        // Beyond Ready, this helper requires null task dates and empty task/objective acceptance
        // criteria. It rejects selected leader, ISO-date, deadline and acceptance-criteria wording
        // in Entry fragments plus digest descriptions; it is not an exhaustive invention detector.
        EvaluationAssertions.ReadyWithoutOptionalInventions(result);
        // Assignment requires each named person near the subject in an Entry fragment. It does
        // not require one task per person or a particular Owner-field representation.
        Assert.Empty(result.Questions);
        /*
        if (scenario == "login-clear") EvaluationAssertions.Assignment(result, "login", "luis", "pablo");
        else if (scenario == "travel-clear") EvaluationAssertions.Assignment(result, "traslado", "ane", "luis");
        else EvaluationAssertions.Assignment(result, "renewal|retention", "priya", "omar");
        */
    }

    [LiveModelFact]
    public async Task OE_01_multiple_plausible_export_references_require_clarification()
    {
        // History distinguishes monthly-billing CSV from signed-report PDF, with separate
        // retention rules. "Remove the old export after the replacement is validated" names
        // neither; existing downloads must remain available. Choosing either export is unjustified.
        var run = CreateRun("OE-01", "ambiguous-export");
        var result = await run.Review();
        EvaluationAssertions.OpenQuestions(result);
        // In addition to the shared open-question schema, at least one question must mention
        // BOTH alternatives across its description/options (case-insensitive substring checks).
        // No fixed question ID/count is prescribed, and this does not grade all removal constraints.
        Assert.Contains(result.Questions, question =>
            Contains(question.Description + " " + string.Join(" ", question.SuggestedOptions ?? []), "CSV")
            && Contains(question.Description + " " + string.Join(" ", question.SuggestedOptions ?? []), "PDF"));
    }

    [LiveModelTheory]
    [InlineData("team-context", false)]
    [InlineData("team-marta", true)]
    public async Task OE_02_assignment_enriches_one_planned_contextual_team_without_replacing_members(string scenario, bool marta)
    {
        // History says a login team was planned, work has not started and roles are unassigned;
        // the second variant already includes Marta. The new note adds Luis and Pablo to that
        // existing team, rather than creating another team or replacing its existing members.
        var run = CreateRun("OE-02", scenario);
        // TeamUpdate applies ReadyWithoutOptionalInventions and login/person proximity checks.
        // Exactly one digest description matching login/team must be an Update (unrelated digest
        // changes are not counted). Entry text must acknowledge prior planning; task statuses must
        // not match completed/active/in-progress wording. Entry plus relevant digest text must not
        // imply exclusive membership, replacement of Marta, leadership or UI/API-specific roles.
        // With Marta, Entry must associate her with prior membership AND the update must name her.
        // These wording checks preserve planned-state enrichment without enforcing a fixed schema.
        EvaluationAssertions.TeamUpdate(await run.Review(), marta);
    }

    [LiveModelTheory]
    [InlineData("software-details")]
    [InlineData("travel-details")]
    [InlineData("business-details")]
    public async Task OE_03_entry_preserves_detail_relations_units_conditions_and_negations(string scenario)
    {
        // Software: Luis owns UI, Pablo API; password-only/no SSO; Friday is tentative and
        // Security approval is required. Lock HUMAN accounts after five failed attempts for ten
        // minutes, exempt service accounts, and retain audit logs thirty days.
        // Travel (Spanish): four passengers, Lisbon airport to lodging only after Ane confirms
        // the address; TOTAL EUR 80 including tolls, no night surcharge; two child seats for ages
        // three/six; one bag per person up to 10 kg; tentative Friday afternoon only if the flight
        // is confirmed; keep the receipt to split costs equally. History does not book the hotel.
        // Business: North-pilot renewal retention 72% to 80%, three complete monthly cohorts,
        // excluding trials; discounts capped at 5%; expansion only after Finance budget approval;
        // keep support involved because unresolved tickets often lead to cancellations.
        var run = CreateRun("OE-03", scenario);
        var result = await run.Review();
        EvaluationAssertions.Ready(result);
        // Facts checks each scenario's lexical relations: owner/subject, quantities AND units,
        // exclusions, conditional wording and rationale, within the fragment boundaries above.
        // Software additionally checks nonblank task Owners: UI-titled/described tasks must name
        // Luis not Pablo, API tasks Pablo not Luis. It does not require Owner to be populated.
        // These patterns constrain loss/swaps but do not establish full semantic equivalence.
        EvaluationAssertions.Facts(scenario, result);
        // No source supplies a firm calendar date. Tentative Friday must not be concretized:
        // every task TargetDate must be null (including the business scenario with no date).
        Assert.All(result.Entry.Tasks, task => Assert.Null(task.TargetDate));
    }

    [LiveModelFact]
    public async Task OE_04_controlled_input_keeps_Q2_open_then_resolves_it_without_losing_Q1_date()
    {
        // Source offers two launch dates (2026-10-16/2026-10-23) and two reporting owners
        // (Luis/Pablo), while fixing password-only/no SSO and thirty-day audit retention.
        // This first path supplies a CONTROLLED INPUT draft, not a canned final model response:
        // one planned rollout task has pending Q1=date and Q2=owner, both initially unresolved.
        var run = CreateRun("OE-04 controlled previous_result", "date-owner-answers");
        var dateOnly = new Dictionary<string, string> { ["Q1"] = LiveEvaluationFixtures.DateAnswer };
        var partial = await run.Review(LiveEvaluationFixtures.ControlledPreviousResult(), dateOnly);
        // DateAnswer selects 2026-10-23 only. PartialAnswers requires exactly open Q2, an
        // owner/reporting question, no pending Q1, and unchanged login/password/no-SSO/log facts.
        // The chosen date needs locally linked user-answer provenance; no task may use the
        // rejected date. Both owner alternatives must remain visible, with neither an exact
        // Luis/Pablo task Owner nor selected affirmative ownership wording in Entry fragments.
        EvaluationAssertions.PartialAnswers(partial);

        // Only Q2 is answered now: Q1's date must survive from the actual partial draft.
        // OwnerAnswer selects Pablo and explicitly excludes Luis; the original source is still
        // sent too. CompletedAnswers requires Ready, the same source/date checks, Pablo near
        // reporting plus affirmative, user-attributed reporting ownership, and no reporting task
        // whose Owner names Luis. Controlled IDs attribute the date to Q1 and ownership to Q2.
        var complete = await run.Review(partial, new Dictionary<string, string> {
            ["Q2"] = LiveEvaluationFixtures.OwnerAnswer
        });
        EvaluationAssertions.CompletedAnswers(complete);
    }

    [LiveModelFact]
    public async Task OE_04_actual_first_result_roundtrip_answers_its_real_IDs_without_prescribing_a_question_split()
    {
        // Same date/owner ambiguity, but NO controlled draft: the first real model result must
        // ask questions and satisfy OpenQuestions' ID/pending-reference schema. This exercises
        // the production roundtrip without assuming the model splits date and owner into Q1/Q2.
        var run = CreateRun("OE-04 actual first result", "date-owner-answers");
        var first = await run.Review();
        EvaluationAssertions.OpenQuestions(first);
        // A model may group the two ambiguities. Answer the actual questions, not invented IDs.
        var answers = first.Questions.ToDictionary(question => question.Id,
            _ => LiveEvaluationFixtures.DateAnswer + " " + LiveEvaluationFixtures.OwnerAnswer);
        var final = await run.Review(first, answers);
        // Every submitted answer above includes both clarifications. Use the actual answered IDs
        // for both facts rather than guessing which question the model assigned to date or owner.
        // CompletedAnswers has the same readiness/source/date/owner contract as the controlled
        // path. Either fact may cite any actually answered ID because every answer contains both.
        //
        // Provenance is stricter than mere repetition of a chosen date/name: ClarifiedAnswer
        // splits EACH Entry field independently at sentence punctuation/newlines and requires
        // the fact and attribution in the SAME statement. Task fields are not joined here, so
        // a date answer in Details cannot establish user provenance for a separate Owner field.
        // Attribution accepts "user answer/confirmed/clarified" wording or an answered Q-ID with
        // answer/resolution wording (also a leading "Qn:"). Explicit IDs that match NONE of the
        // relevant answered IDs are rejected even if generic user-clarification wording exists.
        // Questions/digest metadata do not count. This local lexical boundary can reject valid
        // cross-sentence paraphrases; it does not independently prove the attribution is true.
        var answeredIds = answers.Keys.ToArray();
        EvaluationAssertions.CompletedAnswers(final, dateAnswerIds: answeredIds, ownerAnswerIds: answeredIds);
    }

    private LiveRun CreateRun(string evaluation, string scenario)
    {
        // Recheck opt-in before resolving credentials: environment first, then dedicated test
        // User Secrets if blank. No launchSettings lookup occurs in this test. Missing credentials
        // fail before invoking a model; the secret is never written to test output.
        var key = LiveModelCredentials.ResolveForLiveExecution(Environment.GetEnvironmentVariable(LiveModelGate.Variable));
        output.WriteLine($"evaluation={evaluation}; scenario={scenario}; requested_model={Model}; provider=OpenAI; transport=real; harness=isolated");
        // Each run gets fresh immutable synthetic entries, snapshots, read evidence and tracker.
        // Shared fixture IDs describe conversation boundaries, not previously saved model output.
        var fixture = LiveEvaluationFixtures.Get(scenario);
        var repository = fixture.CreateRepository();
        var tracker = new FunctionCallTracker();
        // Explicit opt-in isolation disables file memory, cwd skills, todo/mode and hosted web.
        // File access/background agents are not configured. No application API or database is
        // involved, but OpenAI IS a real external network dependency: isolation is not a sandbox
        // or a no-cost transport. SDK chat -> CustomOpenAIClient -> LocalCustomHarnessAgent ->
        // production OptimizeEntryAgent executes real paid inference; only context tools are fake.
        var transport = new OpenAIClient(key).GetChatClient(Model).AsIChatClient();
        var provider = new CustomOpenAIClient(transport, isolateHarnessFacilities: true);
        var optimizer = new OptimizeEntryAgent(repository.SelectAnchor(fixture.TimelineId, fixture.CurrentEntryId),
            new LocalCustomHarnessAgent(provider), tracker);
        return new(fixture, repository, tracker, optimizer, output);
    }

    private static bool Contains(string text, string fragment) => text.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    private sealed class LiveRun(ConversationFixture fixture, FakeConversationRepository repository,
        FunctionCallTracker tracker, OptimizeEntryAgent optimizer, ITestOutputHelper output)
    {
        public async Task<EntryReviewResult> Review(EntryReviewResult? previous = null,
            Dictionary<string, string>? answers = null)
        {
            // The same run is reused across clarification turns; slice evidence so EACH turn
            // must independently reload context rather than inheriting a previous turn's reads.
            var start = repository.Reads.Count;
            var trackerStart = tracker.Tools.Count;
            try
            {
                // Always keep ORIGINAL source alongside the draft; never replace it with previous_result.
                // Current identity/title/content, actual prior draft and supplied answers travel in
                // one request. Thus follow-up answers refine the source instead of overwriting it.
                var message = JsonSerializer.Serialize(new {
                    current_entry_id = fixture.CurrentEntryId,
                    new_entry = new { title = fixture.Current.Title, content = fixture.Current.Content },
                    previous_result = previous, user_answers = answers
                }, Json);
                var response = await optimizer.RunAsync(message);
                var result = response.Result;
                // Log the actual structured model output for human inspection, not a fixture answer.
                // Lexical oracle success alone cannot certify semantic fidelity or model quality.
                output.WriteLine("actual_structured_response=" + JsonSerializer.Serialize(result, Json));
                // Persisted entries AND snapshots must remain byte-for-byte equivalent in their
                // serialized state: review proposes a draft, never applies it. Read/tracker evidence
                // is deliberately outside this comparison and may accumulate across turns.
                Assert.Equal(repository.OriginalState, repository.CaptureState());
                var reads = repository.Reads.Skip(start).ToArray();
                // All corpus variants deliberately shuffle storage: snapshot covers sequence 10,
                // readable history is 20 and current is 30; future 40 and another timeline's 25
                // are forbidden. Derive chronological expected IDs rather than trusting storage order.
                var expected = fixture.Entries.Where(entry => entry.TimelineId == fixture.TimelineId && entry.Sequence is 20 or 30)
                    .OrderBy(entry => entry.Sequence).Select(entry => entry.Id).ToArray();
                // Exactly one ID-list read followed by one content fetch per allowed entry,
                // in chronological order, with neither duplication nor out-of-window retrieval.
                Assert.Equal(expected.Length + 1, reads.Length);
                Assert.Equal("get_entry_ids", reads[0].Function);
                Assert.Equal(Enumerable.Repeat("fetch_entry", expected.Length), reads.Skip(1).Select(read => read.Function));
                Assert.Equal(expected, reads.Skip(1).Select(read => read.EntryId!.Value));
                // Cross-check application tracker against actual repository reads: same count
                // and names/order, no hidden extra application-context attempts in this turn.
                var calls = tracker.Tools.Skip(trackerStart).ToArray();
                Assert.Equal(reads.Length, calls.Length);
                Assert.Equal(reads.Select(read => read.Function), calls.Select(call => call.Name));
                // These context loads are executed application calls, NOT model-requested SDK
                // tool calls: SDK IDs must be null, execution true and recorded exceptions null.
                Assert.All(calls, call => { Assert.Null(call.ID); Assert.True(call.Executed); Assert.Null(call.Exception); });
                // ID discovery has no arguments and returns a typed Guid sequence equal to the
                // allowed window; a stringified or unordered substitute is not acceptable.
                Assert.Empty(calls[0].Args);
                Assert.Equal(expected, Assert.IsAssignableFrom<IEnumerable<Guid>>(calls[0].Result));
                // Every fetch carries exactly one named entryId argument of actual Guid type,
                // matching its chronological entry; its tracked result must be a string exactly
                // equal to that fixture's persisted content, not invented or transformed context.
                for (var index = 0; index < expected.Length; index++)
                {
                    var argument = Assert.Single(calls[index + 1].Args);
                    Assert.Equal("entryId", argument.Name);
                    Assert.Equal(expected[index], Assert.IsType<Guid>(argument.Value));
                    Assert.Equal(fixture.Entries.Single(entry => entry.Id == expected[index]).Content,
                        Assert.IsType<string>(calls[index + 1].Result));
                }
                return result;
            }
            finally
            {
                // Whitelisted synthetic IDs, function names and error flags only. Never log keys,
                // transport headers, raw provider exceptions, local paths or other process data.
                foreach (var read in repository.Reads.Skip(start))
                    output.WriteLine($"context_read={read.Function}; entry={read.EntryId}");
                foreach (var call in tracker.Tools.Skip(trackerStart))
                    output.WriteLine($"application_context_attempt; sdk_id_present={call.ID is not null}; name={call.Name}; executed={call.Executed}; failed={call.Exception is not null}");
            }
        }
    }
}
