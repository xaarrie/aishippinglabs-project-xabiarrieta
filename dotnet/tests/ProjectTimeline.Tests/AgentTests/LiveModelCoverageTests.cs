using System.Reflection;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.Tests.AgentTests.Support;
using Xunit;

namespace ProjectTimeline.Tests.AgentTests;

public sealed class LiveModelCoverageTests
{
    private static readonly IReadOnlyDictionary<Type, string> Suites = new Dictionary<Type, string>
    {
        [typeof(SearchEntriesAgent)] = "SearchEntriesLiveModelTests",
        [typeof(TimelineFactExtractionAgent)] = "TimelineFactExtractionLiveModelTests",
        [typeof(TimelineFactOptimizationAgent)] = "TimelineFactOptimizationLiveModelTests",
        [typeof(TimelineFactSemanticVerifierAgent)] = "TimelineFactSemanticVerifierLiveModelTests"
    };

    [Theory]
    [InlineData("multi-claim")]
    [InlineData("quantities-units")]
    [InlineData("date-condition-negation")]
    [InlineData("ambiguity-duplicate")]
    [InlineData("empty")]
    public void Extraction_oracles_accept_canonical_fixtures(string id)
    {
        var scenario = FactAgentLiveFixtures.Extraction(id);
        FactAgentLiveAssertions.Extraction(scenario, scenario.Canonical);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("units")]
    [InlineData("quantity")]
    [InlineData("negation")]
    [InlineData("condition")]
    [InlineData("uncertainty")]
    [InlineData("duplicate")]
    [InlineData("invention")]
    public void Extraction_oracles_reject_missing_or_changed_meaning(string mutation)
    {
        var id = mutation switch
        {
            "units" or "quantity" => "quantities-units",
            "negation" or "condition" => "date-condition-negation",
            "uncertainty" => "ambiguity-duplicate",
            _ => "multi-claim"
        };
        var scenario = FactAgentLiveFixtures.Extraction(id);
        var changed = scenario.Canonical.ToList();
        switch (mutation)
        {
            case "missing": changed.RemoveAt(0); break;
            case "units": changed[0] = changed[0] with { Claim = changed[0].Claim.Replace("liters", "gallons") }; break;
            case "quantity": changed[0] = changed[0] with { Claim = changed[0].Claim.Replace("24", "25") }; break;
            case "negation": changed[1] = changed[1] with { Claim = changed[1].Claim.Replace("not ", "") }; break;
            case "condition": changed[0] = changed[0] with { Claim = changed[0].Claim.Replace(" only if Nora approves it", "") }; break;
            case "uncertainty": changed[0] = changed[0] with { Claim = changed[0].Claim.Replace("may be", "is") }; break;
            case "duplicate": changed.Add(changed[0]); break;
            case "invention": changed.Add(FactAgentLiveFixtures.Candidate("In project Atlas, car C-7 is blue.")); break;
        }
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => FactAgentLiveAssertions.Extraction(scenario, changed));
    }

    [Fact]
    public void Extraction_oracles_allow_combined_claims_and_approximate_draft_quotes()
    {
        var scenario = FactAgentLiveFixtures.Extraction("multi-claim");
        var combined = scenario.Canonical[0] with
        {
            Claim = string.Join(" ", scenario.Canonical.Select(c => c.Claim)),
            SourceQuote = "Atlas register describes a red C-7 owned by Nora."
        };
        FactAgentLiveAssertions.Extraction(scenario, [combined]);
    }

    [Theory]
    [InlineData("stored")]
    [InlineData("measured")]
    [InlineData("required")]
    [InlineData("squared")]
    public void Sequential_extraction_allows_benign_marker_substrings(string word)
    {
        var candidate = FactAgentLiveFixtures.Extraction("quantities-units").Canonical[0];
        FactAgentLiveAssertions.NoPreviousEntryMarkers([candidate with { Claim = word }]);
        FactAgentLiveAssertions.NoPreviousEntryMarkers([candidate with { SourceQuote = word }]);
        FactAgentLiveAssertions.NoPreviousEntryMarkers([candidate with { EntityKey = "atlas_" + word }]);
    }

    [Fact]
    public void Sequential_extraction_allows_valid_stored_equipment_claim()
    {
        var scenario = FactAgentLiveFixtures.Extraction("quantities-units");
        var candidates = scenario.Canonical.ToArray();
        candidates[0] = candidates[0] with { Claim = "In project Atlas, 24 liters of coolant are stored in tank R-2." };
        FactAgentLiveAssertions.Extraction(scenario, candidates);
        FactAgentLiveAssertions.NoPreviousEntryMarkers(candidates);
    }

    [Theory]
    [InlineData("Nora")]
    [InlineData("nOrA")]
    [InlineData("C-7")]
    [InlineData("c-7")]
    [InlineData("red")]
    [InlineData("RED")]
    public void Sequential_extraction_rejects_actual_previous_entry_markers(string marker)
    {
        var candidate = FactAgentLiveFixtures.Extraction("quantities-units").Canonical[0];
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => FactAgentLiveAssertions.NoPreviousEntryMarkers(
            [candidate with { Claim = "Prior entry: (" + marker + ")." }]));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => FactAgentLiveAssertions.NoPreviousEntryMarkers(
            [candidate with { SourceQuote = "Prior entry: (" + marker + ")." }]));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => FactAgentLiveAssertions.NoPreviousEntryMarkers(
            [candidate with { EntityKey = "atlas_" + marker + "_equipment" }]));
    }

    [Fact]
    public async Task Synthetic_generation_is_current_unaccepted_and_does_not_embed()
    {
        await using var f = await FactAgentLiveDatabase.CreateAsync();
        var old = await f.AcceptAsync("Nora owns Atlas delivery.");
        const string claim = "Liam may own Atlas delivery.";
        var entry = await f.EntryAsync(claim);
        await f.GenerateAsync(entry, FactAgentLiveFixtures.Candidate(claim));
        Assert.True(old.Valid);
        Assert.NotEqual(old.EntryId, entry.Id);
        Assert.False(f.Db.ChangeTracker.HasChanges());
    }

    [Fact]
    public void Every_retained_business_agent_has_a_discoverable_gated_nonparallel_suite()
    {
        var agents = typeof(SearchEntriesAgent).Assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract
            && !t.IsGenericType && t.Namespace is { } ns
            && (ns == typeof(SearchEntriesAgent).Namespace || ns.StartsWith(typeof(SearchEntriesAgent).Namespace + ".", StringComparison.Ordinal))
            && !ns.Contains(".Framework", StringComparison.Ordinal) && !ns.Contains(".Tools", StringComparison.Ordinal)
            && t.Name.EndsWith("Agent", StringComparison.Ordinal)).OrderBy(t => t.FullName).ToArray();
        Assert.Equal(agents, Suites.Keys.OrderBy(t => t.FullName).ToArray());
        foreach (var name in Suites.Values)
        {
            // Metadata only: never construct suites, agents, clients or enumerate provider-backed data.
            var suite = GetType().Assembly.GetType($"{GetType().Namespace}.{name}");
            Assert.NotNull(suite);
            Assert.Contains(suite.CustomAttributes, a => a.AttributeType == typeof(CollectionAttribute)
                && Equals(a.ConstructorArguments[0].Value, IsolatedAgentCollection.Name));
            Assert.Contains(suite.CustomAttributes, a => a.AttributeType == typeof(TraitAttribute)
                && Equals(a.ConstructorArguments[0].Value, "Category") && Equals(a.ConstructorArguments[1].Value, "LiveModel"));
            var methods = suite.GetMethods().Where(m => m.CustomAttributes.Any(a => typeof(FactAttribute).IsAssignableFrom(a.AttributeType))).ToArray();
            var expectedCases = name switch
            {
                "SearchEntriesLiveModelTests" => 3,
                "TimelineFactExtractionLiveModelTests" => 6,
                "TimelineFactOptimizationLiveModelTests" => 1,
                "TimelineFactSemanticVerifierLiveModelTests" => 6,
                _ => throw new InvalidOperationException("Missing expected case count.")
            };
            Assert.Equal(expectedCases, methods.Length);
            Assert.Empty(suite.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));
            foreach (var method in methods)
            {
                var fact = Assert.Single(method.CustomAttributes, a => typeof(FactAttribute).IsAssignableFrom(a.AttributeType));
                Assert.True(fact.AttributeType == typeof(LiveModelFactAttribute) || fact.AttributeType == typeof(LiveModelTheoryAttribute), method.Name);
                Assert.DoesNotContain(method.CustomAttributes, a => a.AttributeType == typeof(MemberDataAttribute));
                if (fact.AttributeType == typeof(LiveModelTheoryAttribute))
                    Assert.Contains(method.CustomAttributes, a => a.AttributeType == typeof(InlineDataAttribute));
            }
        }
        Assert.True(typeof(IsolatedAgentCollection).GetCustomAttribute<CollectionDefinitionAttribute>()!.DisableParallelization);
    }
}
