using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.DataAccessLogic;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class TimelineFactFullTextSearchTests
{
    private const string Previous = "20261018000300_FactInvalidationCause";
    private const string Migration = "20261025163000_TimelineFactFullTextIndex";
    private const string Index = "IX_TimelineEntryFacts_FullText";
    private static ProjectTimelineDbContext Offline() => new(new DbContextOptionsBuilder<ProjectTimelineDbContext>()
        .UseNpgsql("Host=localhost;Database=offline_sql_only;Username=unused",
            x => x.MigrationsAssembly("ProjectTimeline.Database")).Options);

    private static IQueryable<TimelineFactSearchHit> Query(ProjectTimelineDbContext db, string path, string[] terms, int take,
        TimelineFactReviewFixture? fixture = null)
    {
        object search = path == "Review" ? new TimelineFactSearch(db, new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid(), false)
            : new TimelineFactInformationSearch(db, new(fixture?.Project.Id ?? Guid.NewGuid(), fixture?.Timeline.Id ?? Guid.NewGuid()), Guid.NewGuid(), false);
        var name = path == "Research" ? "ResearchQuery" : "SearchQuery";
        return (IQueryable<TimelineFactSearchHit>)search.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(search, path == "Review" ? [2, terms, take] : [terms, take])!;
    }

    [Theory]
    [InlineData("Review")]
    [InlineData("Information")]
    [InlineData("Research")]
    public void Provider_SQL_has_parameterized_OR_rank_scope_and_exact_index_vector(string path)
    {
        using var db = Offline();
        var sql = Query(db, path, ["alice", "operator') | !needle:*"], 3).ToQueryString();
        var statement = sql[sql.IndexOf("SELECT", StringComparison.Ordinal)..];
        Assert.Contains("plainto_tsquery('simple', @", statement);
        Assert.Contains(" || plainto_tsquery('simple', @", statement);
        Assert.Contains("@@", statement); Assert.Contains("ORDER BY ts_rank(", statement);
        Assert.Contains("DESC", statement); Assert.Contains("LIMIT @", statement);
        Assert.Matches("EntryNumber\" DESC, .*\"Id\"", statement);
        Assert.DoesNotContain("needle", statement); Assert.DoesNotContain("alice", statement);
        Assert.Contains("operator", sql[..sql.IndexOf("SELECT", StringComparison.Ordinal)]);
        Assert.Contains("Accepted", statement); Assert.Contains("Completed", statement);
        Assert.Contains("ProjectId", statement); Assert.Contains("TimelineId", statement); Assert.Contains("Valid", statement);
        Assert.DoesNotContain("\"Content\"", statement); Assert.DoesNotContain("Embedding", statement);
        Assert.Equal(path == "Review", statement.Contains("EntryNumber\" <="));
        // Compare generated provider DDL with the runtime expression (aliases are irrelevant).
        var up = db.GetService<IMigrator>().GenerateScript(Previous, Migration);
        var vector = Regex.Match(up, @"USING gin \((to_tsvector\([^\r\n]+)\);").Groups[1].Value;
        Assert.Equal("to_tsvector('simple', \"Claim\" || ' ' || \"EntityKey\" || ' ' || \"EntityType\")", vector);
        var unqualified = Regex.Replace(statement, @"\b\w+\.""", "\"");
        Assert.Contains(vector + " @@", unqualified);
        Assert.Contains("ORDER BY ts_rank(" + vector + ",", unqualified);
        Assert.DoesNotContain("SourceQuote", vector); Assert.DoesNotContain("ClarificationEvidence", vector);
        Assert.Contains("FALSE", Query(db, path, [], 3).ToQueryString());
        Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Fact]
    public void Migration_is_index_only_reversible_and_matches_frozen_and_runtime_models()
    {
        using var db = Offline();
        var migrator = db.GetService<IMigrator>();
        var up = migrator.GenerateScript(Previous, Migration);
        var down = migrator.GenerateScript(Migration, Previous);
        Assert.Contains($"CREATE INDEX \"{Index}\" ON \"TimelineEntryFacts\" USING gin", up);
        Assert.Contains("to_tsvector('simple',", up);
        Assert.Contains($"DROP INDEX \"{Index}\"", down);
        Assert.DoesNotContain("ADD COLUMN", up); Assert.DoesNotContain("DROP COLUMN", down);
        Assert.DoesNotContain("UPDATE \"TimelineEntryFacts\"", up);
        Assert.False(db.Database.HasPendingModelChanges());
        foreach (var field in new[] { "Claim", "EntityKey", "EntityType" })
            Assert.False(db.Model.FindEntityType(typeof(TimelineFact))!.FindProperty(field)!.IsNullable);
        Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Theory]
    [InlineData("Review")]
    [InlineData("Information")]
    [InlineData("Research")]
    public async Task Approximate_InMemory_seam_has_OR_lexical_rank_stable_ties_limits_and_excluded_fields(string path)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        f.Entry.Content += " Entrybodyonly."; await f.Db.SaveChangesAsync();
        var first = await f.AcceptAsync("Alice owns delivery.", "alice");
        await f.NewSourceAsync();
        var recent = await f.AcceptAsync("Bob owns testing.", "bob");
        var twinId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        await using (var raw = f.Unguarded())
        {
            raw.Add(new TimelineFact { Id = twinId, EntryId = first.EntryId, CreationReviewId = first.CreationReviewId,
                AdditionId = Guid.NewGuid(), Claim = first.Claim, EntityKey = first.EntityKey + " keyonly", EntityType = first.EntityType + " typeonly",
                SourceQuote = "hiddenquote", ClarificationEvidence = "hiddenclarification", SourceFingerprint = first.SourceFingerprint });
            await raw.SaveChangesAsync();
        }
        // Use real public searches for authorization/scope; private query only for research's counted-tool seam.
        async Task<IReadOnlyList<TimelineFactSearchHit>> Find(string text, int limit = 20) => path switch
        {
            "Review" => await f.Search.SearchAsync(text, limit),
            "Information" => (await new TimelineFactInformationSearch(f.Db, new(f.Project.Id, f.Timeline.Id), f.Actor.Id, false)
                .SearchAsync(text, limit)).Facts,
            _ => await Query(f.Db, "Research", TimelineTerms(text), limit, f).ToListAsync()
        };
        Assert.Empty(await Find("alic")); Assert.Empty(await Find("deliv"));
        var ranked = await Find("alice delivery testing");
        Assert.Equal(3, ranked.Count); Assert.Equal(twinId, ranked[0].Id); Assert.Equal(first.Id, ranked[1].Id);
        Assert.Equal(recent.Id, ranked[2].Id); Assert.Equal(twinId, Assert.Single(await Find("alice delivery testing", 1)).Id);
        Assert.Equal(recent.Id, (await Find("owns"))[0].Id); // equal rank: newest entry first
        Assert.Equal(3, (await Find("Person")).Count);
        Assert.Equal(twinId, Assert.Single(await Find("keyonly")).Id);
        Assert.Equal(twinId, Assert.Single(await Find("typeonly")).Id);
        Assert.Empty(await Find("Entrybodyonly"));
        Assert.Empty(await Find("hiddenquote")); Assert.Empty(await Find("hiddenclarification"));
        Assert.Empty(await Find("Original")); // entry title is never searchable
        Assert.Empty(await Find("Entry")); // SourceKind is not searchable
        Assert.Empty(await Find(first.SourceFingerprint));
        Assert.Equal(2, (await Find("alice | !missing:*")).Count); // operators are data, not negation
        Assert.Equal(first.SourceFingerprint, ranked[1].SourceFingerprint);
        Assert.Equal(first.CreationReviewId, ranked[1].CreationReviewId);
    }

    private static string[] TimelineTerms(string text) => (string[])typeof(TimelineFactSearch)
        .GetMethod("Terms", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [text])!;

    [Fact]
    public async Task Empty_and_punctuation_never_match_all_and_terms_remain_bounded()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        foreach (var text in new[] { "", "!?", "--", "__" })
        {
            Assert.Empty(await f.Search.SearchAsync(text));
            Assert.Empty(await Query(f.Db, "Research", TimelineTerms(text), 20, f).ToListAsync());
            Assert.Throws<ArgumentException>(() => TimelineFactInformationSearch.ValidateQuestion(text));
        }
        Assert.Equal(12, TimelineTerms(string.Join(" ", Enumerable.Range(0, 20).Select(x => "term" + x))).Length);
        Assert.Single(TimelineTerms("ALICE alice Alice"));
        Assert.Throws<ArgumentException>(() => TimelineFactInformationSearch.ValidateQuestion(new string('a', 301)));
    }
}
