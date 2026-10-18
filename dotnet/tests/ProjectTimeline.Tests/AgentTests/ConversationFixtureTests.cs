using ProjectTimeline.Tests.AgentTests.Support;
using Xunit;

namespace ProjectTimeline.Tests.AgentTests;

// Corpus integrity checks only; expected/forbidden fact metadata is not a model-quality score.
public sealed class ConversationFixtureTests
{
    [Theory]
    [InlineData("software-functionality")]
    [InlineData("software-ambiguous-control")]
    [InlineData("travel-detailed-conditions")]
    [InlineData("business-partial-answers")]
    public async Task Expected_facts_have_readable_source_provenance_and_context_facts_stay_separate(string fixtureId)
    {
        var fixture = ConversationFixtures.Get(fixtureId);
        var tools = fixture.CreateRepository().SelectAnchor(fixture.TimelineId, fixture.CurrentEntryId);
        var allowed = await tools.get_entry_ids();
        Assert.Equal(fixture.CurrentEntryId, allowed.Last());
        Assert.NotEmpty(fixture.Current.Title);
        Assert.NotEmpty(fixture.ExpectedFacts);
        Assert.NotEmpty(fixture.ContextOnlyFacts);
        Assert.NotEmpty(fixture.ForbiddenFacts);
        Assert.NotEmpty(fixture.UnspecifiedOptionalFields);
        var allFacts = fixture.ExpectedFacts.Concat(fixture.ContextOnlyFacts).ToArray();
        Assert.Equal(allFacts.Length, allFacts.Select(fact => fact.Key).Distinct().Count());
        foreach (var fact in allFacts)
        {
            Assert.Contains(fact.SourceEntryId, allowed);
            Assert.Contains(fact.Text, await tools.fetch_entry(fact.SourceEntryId));
        }
        Assert.All(fixture.ExpectedFacts, fact => Assert.Equal(fixture.CurrentEntryId, fact.SourceEntryId));
        Assert.All(fixture.ContextOnlyFacts, fact => Assert.NotEqual(fixture.CurrentEntryId, fact.SourceEntryId));
        Assert.All(fixture.ForbiddenFacts, fact => {
            Assert.NotEmpty(fact.Reason);
            Assert.DoesNotContain(fact.Text, fixture.Current.Content);
        });
        Assert.All(fixture.Clarifications, question => Assert.Contains(question.SourceQuote, fixture.Current.Content));
    }

    [Fact]
    public void Corpus_covers_three_domains_two_languages_optional_fields_and_a_genuine_ambiguity_control()
    {
        Assert.Equal(["business-objectives", "software-project", "travel-preparation"],
            ConversationFixtures.All.Select(fixture => fixture.Domain).Distinct().Order());
        Assert.Contains(ConversationFixtures.All, fixture => fixture.Language == "en");
        Assert.Contains(ConversationFixtures.All, fixture => fixture.Language == "es");
        var optional = ConversationFixtures.Get("software-functionality");
        Assert.Empty(optional.Clarifications);
        Assert.Contains("targetDate", optional.UnspecifiedOptionalFields);
        Assert.Contains(optional.ExpectedFacts, fact => fact.Key == "team-update" && fact.Text.Contains("Marta", StringComparison.Ordinal));
        var ambiguous = ConversationFixtures.Get("software-ambiguous-control");
        Assert.Single(ambiguous.Clarifications);
        Assert.Equal(2, ambiguous.ContextOnlyFacts.Count);
        Assert.NotNull(ambiguous.CompleteAnswers);
    }

    [Fact]
    public void Partial_and_complete_answer_rounds_address_only_known_stable_question_IDs()
    {
        var fixture = ConversationFixtures.Get("business-partial-answers");
        Assert.NotNull(fixture.PartialAnswers);
        Assert.NotNull(fixture.CompleteAnswers);
        var questionIds = fixture.Clarifications.Select(question => question.Id).ToArray();
        Assert.Equal(["Q1", "Q2"], questionIds);
        Assert.Equal(["Q1"], fixture.PartialAnswers.Answers.Keys);
        Assert.Equal(["Q2"], fixture.PartialAnswers.UnresolvedQuestionIds);
        Assert.Equal(questionIds, fixture.CompleteAnswers.Answers.Keys);
        Assert.Empty(fixture.CompleteAnswers.UnresolvedQuestionIds);
        Assert.Equal(fixture.PartialAnswers.Answers["Q1"], fixture.CompleteAnswers.Answers["Q1"]);
        Assert.DoesNotContain("Q2", fixture.PartialAnswers.Answers.Keys);
        Assert.Contains("Priya", fixture.CompleteAnswers.Answers["Q2"]);
    }
}
