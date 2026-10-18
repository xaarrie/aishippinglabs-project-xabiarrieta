using ProjectTimeline.BusinessLogic.Agents.AgentModels;
using ProjectTimeline.Tests.AgentTests.Support;
using Xunit;
using Xunit.Sdk;

namespace ProjectTimeline.Tests.AgentTests;

// Tests of the lexical/structural ORACLE, not model evaluations or semantic verification.
public sealed class EvaluationOracleTests
{
    public static IEnumerable<object[]> FactControls()
    {
        yield return ["software-details", "ui-owner", "Luis will implement the login UI.", "Pablo will implement the login UI."];
        yield return ["software-details", "api-owner", "Pablo will implement the login API.", "Luis will implement the login API."];
        yield return ["software-details", "password-only", "Use password-only authentication.", "Use password authentication alongside SSO."];
        yield return ["software-details", "no-sso", "No SSO.", "Enable SSO."];
        yield return ["software-details", "tentative-friday", "The launch might be Friday.", "The launch is confirmed for Friday."];
        yield return ["software-details", "security-approval", "Launch only if Security approves it.", "Launch regardless of Security approval."];
        yield return ["software-details", "attempts", "Lock human accounts after five failed login attempts.", "Lock human accounts after ten failed login attempts."];
        yield return ["software-details", "lock-duration", "Lock human accounts for ten minutes.", "Lock human accounts for ten hours."];
        yield return ["software-details", "service-exemption", "Service accounts are exempt from this lockout.", "Service accounts are subject to this lockout."];
        yield return ["software-details", "audit-retention", "Retain audit logs for thirty days.", "Retain audit logs for thirty hours."];
        yield return ["travel-details", "passengers", "Traslado para 4 personas desde el aeropuerto de Lisboa al alojamiento.", "Traslado para 3 personas desde el aeropuerto de Lisboa al alojamiento."];
        yield return ["travel-details", "address-condition", "Reservar solo cuando Ane confirme la dirección.", "Reservar antes de que Ane confirme la dirección."];
        yield return ["travel-details", "total-budget", "Límite total: 80 €, incluidos peajes.", "80 € por persona, incluidos peajes."];
        yield return ["travel-details", "no-surcharge", "No aceptar suplementos nocturnos.", "Aceptar suplementos nocturnos."];
        yield return ["travel-details", "child-seats", "Dos sillas infantiles, una para 3 años y otra para 6 años.", "Una silla infantil para 3 años."];
        yield return ["travel-details", "luggage", "Cada persona llevará una maleta de hasta 10 kg.", "Una maleta de hasta 10 kg para todo el grupo."];
        yield return ["travel-details", "tentative-arrival", "La llegada sería quizá el viernes por la tarde, si se confirma el vuelo.", "La llegada está confirmada el viernes por la tarde."];
        yield return ["travel-details", "receipt-sharing", "Conservar el justificante para repartir el gasto a partes iguales.", "Conservar el justificante sin repartir el gasto."];
        yield return ["business-details", "retention-range", "Improve renewal retention from 72% to 80% in the North pilot.", "Improve renewal retention from 70% to 80% in the South pilot."];
        yield return ["business-details", "cohorts", "Measure over three complete monthly cohorts.", "Measure over three weekly cohorts."];
        yield return ["business-details", "exclude-trials", "Measure retention excluding trial accounts.", "Measure retention including trial accounts."];
        yield return ["business-details", "discount-cap", "Do not increase discounts above 5%.", "Increase discounts above 5%."];
        yield return ["business-details", "finance-condition", "Expand only after Finance approves the budget.", "Expand before Finance approves the budget."];
        yield return ["business-details", "support-rationale", "Keep the support team involved because cancellations often start with unresolved tickets.", "Keep the support team involved."];
    }

    [Theory]
    [MemberData(nameof(FactControls))]
    public void Relation_oracle_accepts_positive_and_rejects_mutation_missing_fact_and_metadata_echo(
        string scenario, string key, string positive, string mutation)
    {
        EvaluationAssertions.Fact(scenario, key, Draft(positive));
        Assert.ThrowsAny<XunitException>(() => EvaluationAssertions.Fact(scenario, key, Draft(mutation)));
        // Moving a fact into the summary or questions must NOT count as Entry coverage.
        var echo = Draft() with { ChangeSummary = positive,
            Questions = [new("Q1", QuestionType.MissingData, Impact.Low, positive, positive, null, [positive])] };
        Assert.ThrowsAny<XunitException>(() => EvaluationAssertions.Fact(scenario, key, echo));
    }

    [Theory]
    [InlineData("software-details")]
    [InlineData("travel-details")]
    [InlineData("business-details")]
    public void Complete_fact_set_passes_but_each_individual_omission_fails(string scenario)
    {
        var controls = FactControls().Where(row => (string)row[0] == scenario).ToArray();
        var details = controls.Select(row => (string)row[2]).ToArray();
        EvaluationAssertions.Facts(scenario, Draft(details));
        foreach (var omitted in details)
            Assert.ThrowsAny<XunitException>(() => EvaluationAssertions.Facts(scenario,
                Draft(details.Where(text => text != omitted).ToArray()) with { ChangeSummary = omitted }));
    }

    [Fact]
    public void Structured_swapped_owners_are_rejected_even_if_context_echoes_the_correct_assignments()
    {
        var entry = Draft("Luis will implement the login UI.", "Pablo will implement the login API.").Entry with {
            Tasks = [Task("UI", "Pablo"), Task("API", "Luis")]
        };
        Assert.ThrowsAny<XunitException>(() => EvaluationAssertions.CorrectSoftwareOwners(Draft() with { Entry = entry }));
        EvaluationAssertions.CorrectSoftwareOwners(Draft() with { Entry = entry with {
            Tasks = [Task("UI", "Luis"), Task("API", "Pablo")]
        } });
    }

    [Theory]
    [InlineData("unnecessary-question")]
    [InlineData("date")]
    [InlineData("leader")]
    [InlineData("deadline-text")]
    [InlineData("criteria-text")]
    [InlineData("criteria")]
    public void Optional_field_oracle_rejects_unnecessary_questions_and_inventions(string mutation)
    {
        var baseline = Draft("Luis and Pablo will implement the login functionality.") with {
            Entry = Draft().Entry with { Tasks = [Task("login", "Luis and Pablo")] }
        };
        EvaluationAssertions.ReadyWithoutOptionalInventions(baseline);
        var altered = mutation switch {
            "unnecessary-question" => baseline with { Status = Status.NeedsClarification,
                Questions = [new("Q1", QuestionType.MissingData, Impact.Low, "When is the deadline?", null, null, null)] },
            "date" => baseline with { Entry = baseline.Entry with { Tasks = [Task("login", "Luis and Pablo") with { TargetDate = "2026-10-23" }] } },
            "leader" => baseline with { Entry = baseline.Entry with { AdditionalDetails = ["Luis is team leader."] } },
            "deadline-text" => baseline with { Entry = baseline.Entry with { AdditionalDetails = ["Deadline: Friday."] } },
            "criteria-text" => baseline with { Entry = baseline.Entry with { AdditionalDetails = ["Acceptance criteria: all tests pass."] } },
            _ => baseline with { Entry = baseline.Entry with { Tasks = [Task("login", "Luis and Pablo") with { AcceptanceCriteria = ["99% availability"] }] } }
        };
        Assert.ThrowsAny<XunitException>(() => EvaluationAssertions.ReadyWithoutOptionalInventions(altered));
    }

    [Fact]
    public void Explicitly_unspecified_optional_fields_are_not_inventions()
    {
        EvaluationAssertions.ReadyWithoutOptionalInventions(Draft(
            "Luis and Pablo will implement login. No deadline was specified. No team leader was specified."));
    }

    [Theory]
    [InlineData("dangling")]
    [InlineData("renamed")]
    [InlineData("resolved-still-pending")]
    [InlineData("owner-resolved")]
    [InlineData("owner-resolved-in-text")]
    [InlineData("lost-source")]
    public void Partial_answer_oracle_requires_stable_Q2_and_cleared_Q1_without_losing_source(string mutation)
    {
        var previous = LiveEvaluationFixtures.ControlledPreviousResult();
        var partial = previous with {
            Questions = previous.Questions.Where(question => question.Id == "Q2").ToList(),
            Entry = previous.Entry with {
                Tasks = [previous.Entry.Tasks[0] with { TargetDate = "2026-10-23", PendingQuestionIds = ["Q2"] }],
                AdditionalDetails = ["User clarification (Q1): launch date 2026-10-23."]
            }
        };
        EvaluationAssertions.PartialAnswers(partial);
        var altered = mutation switch {
            "dangling" => partial with { Entry = partial.Entry with { Tasks = [partial.Entry.Tasks[0] with { PendingQuestionIds = ["Q9"] }] } },
            "renamed" => partial with { Questions = [partial.Questions[0] with { Id = "Q3" }],
                Entry = partial.Entry with { Tasks = [partial.Entry.Tasks[0] with { PendingQuestionIds = ["Q3"] }] } },
            "resolved-still-pending" => partial with { Entry = partial.Entry with { Tasks = [partial.Entry.Tasks[0] with { PendingQuestionIds = ["Q1", "Q2"] }] } },
            "owner-resolved" => partial with { Entry = partial.Entry with { Tasks = [partial.Entry.Tasks[0] with { Owner = "Luis" }] } },
            "owner-resolved-in-text" => partial with { Entry = partial.Entry with {
                AdditionalDetails = [.. partial.Entry.AdditionalDetails, "Pablo owns reporting."] } },
            _ => partial with { Entry = partial.Entry with { Tasks = [partial.Entry.Tasks[0] with { Details = [] }] } }
        };
        Assert.ThrowsAny<XunitException>(() => EvaluationAssertions.PartialAnswers(altered));
    }

    [Theory]
    [InlineData("lost-date")]
    [InlineData("lost-source")]
    [InlineData("wrong-owner")]
    [InlineData("dangling-pending")]
    [InlineData("question-still-open")]
    public void Completed_answer_oracle_preserves_date_source_owner_and_resolves_all_references(string mutation)
    {
        var baseline = Draft() with {
            Entry = Draft().Entry with {
                Tasks = [Task("rollout and reporting", "Pablo") with {
                    TargetDate = "2026-10-23",
                    Details = ["Original source: password-only authentication, no SSO.",
                        "Retain audit logs for thirty days.",
                        "User clarification (Q1): launch date 2026-10-23.",
                        "User clarification (Q2): Pablo owns reporting; Luis is not the reporting owner."]
                }]
            }
        };
        EvaluationAssertions.CompletedAnswers(baseline);
        var task = baseline.Entry.Tasks[0];
        var altered = mutation switch {
            "lost-date" => baseline with { Entry = baseline.Entry with { Tasks = [task with {
                TargetDate = null, Details = task.Details.Where(detail => !detail.Contains("Q1")).ToList() }] } },
            "lost-source" => baseline with { Entry = baseline.Entry with { Tasks = [task with {
                Details = task.Details.Where(detail => !detail.Contains("password")).ToList() }] } },
            "wrong-owner" => baseline with { Entry = baseline.Entry with { Tasks = [task with { Owner = "Luis" }] } },
            "dangling-pending" => baseline with { Entry = baseline.Entry with { Tasks = [task with { PendingQuestionIds = ["Q2"] }] } },
            _ => baseline with { Questions = [LiveEvaluationFixtures.ControlledPreviousResult().Questions[1]] }
        };
        Assert.ThrowsAny<XunitException>(() => EvaluationAssertions.CompletedAnswers(altered));
    }

    [Theory]
    [InlineData("missing", "Pablo")]
    [InlineData("date-only", "Pablo")]
    [InlineData("wrong-question", "Pablo")]
    [InlineData("wrong-owner", "Pablo")]
    [InlineData("summary-only", "Pablo")]
    [InlineData("adjacent-source", "Pablo")]
    [InlineData("missing", "Ana")]
    [InlineData("date-only", "Ana")]
    [InlineData("wrong-question", "Ana")]
    [InlineData("wrong-owner", "Ana")]
    [InlineData("summary-only", "Ana")]
    [InlineData("adjacent-source", "Ana")]
    public void Completed_owner_must_be_attributed_to_its_answer_not_merely_repeated_from_source(string mutation, string owner)
    {
        var attribution = $"User clarification (Q2): {owner} owns reporting.";
        var baseline = OwnershipDraft(owner, attribution);
        EvaluationAssertions.CompletedAnswers(baseline, expectedOwner: owner);
        var replacement = mutation switch {
            "missing" or "summary-only" => $"{owner} owns reporting.",
            "date-only" => "User clarification (Q1): launch date 2026-10-23.",
            "wrong-question" => $"User clarification (Q1): {owner} owns reporting.",
            "wrong-owner" => "User clarification (Q2): Luis owns reporting.",
            _ => $"User clarification (Q2): launch date 2026-10-23. Original source: {owner} owns reporting."
        };
        var altered = OwnershipDraft(owner, replacement) with { ChangeSummary = attribution };
        Assert.ThrowsAny<XunitException>(() => EvaluationAssertions.CompletedAnswers(altered, expectedOwner: owner));
    }

    [Theory]
    [InlineData("Per the user's response, Ana will take responsibility for reporting.")]
    [InlineData("In answer Q2, reporting will be owned by Ana.")]
    [InlineData("The user confirmed Ana as the reporting owner (Q2).")]
    public void Owner_provenance_accepts_natural_alternative_wording(string attribution)
        => EvaluationAssertions.CompletedAnswers(OwnershipDraft("Ana", attribution), expectedOwner: "Ana");

    [Theory]
    [InlineData("Q7", "Q9")]
    [InlineData("Q4", "Q4")]
    public void Completed_provenance_uses_supplied_answer_IDs_without_requiring_a_question_split(string dateId, string ownerId)
    {
        var ownerAttribution = $"User response ({ownerId}): reporting owner is Pablo.";
        var dateAttribution = $"The user confirmed launch date 2026-10-23 ({dateId}).";
        var baseline = OwnershipDraft("Pablo", ownerAttribution, dateAttribution);
        EvaluationAssertions.CompletedAnswers(baseline, dateAnswerIds: [dateId], ownerAnswerIds: [ownerId]);
        // Both names and the date survive; only the attribution's actual question ID changes.
        var wrongOwnerId = OwnershipDraft("Pablo", ownerAttribution.Replace(ownerId, "Q99"), dateAttribution);
        Assert.ThrowsAny<XunitException>(() => EvaluationAssertions.CompletedAnswers(wrongOwnerId,
            dateAnswerIds: [dateId], ownerAnswerIds: [ownerId]));
        var wrongDateId = OwnershipDraft("Pablo", ownerAttribution, dateAttribution.Replace(dateId, "Q99"));
        Assert.ThrowsAny<XunitException>(() => EvaluationAssertions.CompletedAnswers(wrongDateId,
            dateAnswerIds: [dateId], ownerAnswerIds: [ownerId]));
    }

    [Theory]
    [InlineData("missing-marta")]
    [InlineData("lost-provenance")]
    [InlineData("lost-plan")]
    [InlineData("exclusive-membership")]
    [InlineData("duplicate-team")]
    [InlineData("invented-role")]
    [InlineData("digest-replaces-marta")]
    public void Team_update_oracle_rejects_lost_context_and_membership_or_role_inventions(string mutation)
    {
        var baseline = Draft("Luis and Pablo will work on the login team.") with {
            Entry = Draft("Luis and Pablo will work on the login team.").Entry with {
                Context = "Prior context: a team was planned to implement login. Marta was already a planned member."
            },
            DigestChanges = [new(ChangeAction.Update, null,
                "Update prior planned login team: retain Marta and add Luis and Pablo.", "New assignment")]
        };
        EvaluationAssertions.TeamUpdate(baseline, existingMarta: true);
        var altered = mutation switch {
            "missing-marta" => baseline with { Entry = baseline.Entry with { Context = "Prior context: planned login team." } },
            "lost-provenance" => baseline with { Entry = baseline.Entry with { Context = "A planned login team includes Marta." } },
            "lost-plan" => baseline with { Entry = baseline.Entry with { Context = "Prior context: a login team includes Marta." } },
            "exclusive-membership" => baseline with { Entry = baseline.Entry with { AdditionalDetails = [.. baseline.Entry.AdditionalDetails, "Only members: Luis and Pablo."] } },
            "duplicate-team" => baseline with { DigestChanges = [.. baseline.DigestChanges, baseline.DigestChanges[0]] },
            "invented-role" => baseline with { Entry = baseline.Entry with { AdditionalDetails = [.. baseline.Entry.AdditionalDetails, "Luis owns the login API."] } },
            _ => baseline with { DigestChanges = [baseline.DigestChanges[0] with { Description = "Update login team: now consists of Luis and Pablo." }] }
        };
        Assert.ThrowsAny<XunitException>(() => EvaluationAssertions.TeamUpdate(altered, existingMarta: true));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("1", true)]
    public void Live_gate_requires_exact_opt_in_and_explicit_missing_credentials_fail_before_any_model(string? value, bool enabled)
    {
        Assert.Equal(enabled, LiveModelGate.SkipReason(value) is null);
        var error = Assert.Throws<InvalidOperationException>(() => LiveModelGate.RequireCredentials(false));
        Assert.Contains("preconfigured OPENAI_API_KEY", error.Message);
        Assert.DoesNotContain("review context could not be read", error.Message);
        LiveModelGate.RequireCredentials(true);
    }

    private static EntryReviewResult OwnershipDraft(string owner, string attribution,
        string dateAttribution = "User clarification (Q1): launch date 2026-10-23.") => Draft() with {
        Entry = Draft().Entry with {
            // Ana/Pablo controls are oracle-only inputs, not changes to the live conversation.
            Context = $"Original source: {owner} owns reporting.",
            Tasks = [Task("rollout and reporting", owner) with {
                TargetDate = "2026-10-23",
                Details = ["Original source: password-only authentication, no SSO.",
                    "Retain audit logs for thirty days.",
                    dateAttribution, attribution]
            }]
        }
    };

    private static TaskItem Task(string label, string owner) => new(null, $"Login {label}", $"Implement login {label}", owner,
        "Planned", null, [], [], []);
    private static EntryReviewResult Draft(params string[] details) => new(Status.Ready, [],
        new("Oracle control", "", [], [], [], []) { AdditionalDetails = details.ToList() }, [], "Oracle control only");
}
