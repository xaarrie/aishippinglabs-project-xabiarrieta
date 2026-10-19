using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.UI.WebApi;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class LegacyEntryReviewRemovalTests
{
    [Fact]
    public void Retired_types_and_production_registration_hooks_are_absent()
    {
        var assemblies = new[] { typeof(TimelineFactService).Assembly, typeof(TimelineFactApplication).Assembly };
        string[] retiredTypes = [
            "OptimizeEntryAgent", "OptimizationAgentTools", "IOptimizationAgentTools",
            "EntryReviewService", "IEntryReviewService", "EntryReviewAuditService", "IEntryReviewAuditService",
            "EntryReviewAuditAgent", "OptimizedEntry", "EntryReviewResult", "EntryReviewAuditRequest",
            "EntryReviewAuditResult", "EntryReviewFailure", "EntryReviewRequest", "EntryReviewEndpoints"
        ];
        foreach (var assembly in assemblies)
            Assert.DoesNotContain(assembly.GetTypes(), type => retiredTypes.Contains(type.Name));

        // Program cannot be booted offline: it runs real database initialization. Check its
        // composition directly as well as exercising the retained endpoint mapping below.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "dotnet", "ProjectTimeline.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root.FullName, "dotnet", "src", "ProjectTimeline.UI.WebApi", "Program.cs"));
        Assert.DoesNotContain("MapEntryReview", source);
        Assert.DoesNotContain("IEntryReviewService", source);
        Assert.DoesNotContain("IEntryReviewAuditService", source);
        Assert.DoesNotContain("/review", source);
        Assert.Contains("app.MapTimelineFacts();", source);
        Assert.Contains("builder.Services.AddTimelineFacts(builder.Configuration);", source);
    }

    [Fact]
    public async Task Retired_posts_are_unroutable_while_fact_read_plan_accept_and_authorization_remain_live()
    {
        await using var host = await TimelineFactHttpHost.CreateAsync();
        var path = $"/api/entries/{host.Entry.Id}";
        foreach (var authenticated in new[] { true, false })
        {
            if (!authenticated) host.Client.DefaultRequestHeaders.Remove("X-Test-Actor");
            foreach (var suffix in new[] { "/review", "/review/audit" })
            {
                using var retired = await host.PostAsync(path + suffix, "{}");
                Assert.Equal(HttpStatusCode.NotFound, retired.StatusCode);
            }
            using var facts = await host.Client.GetAsync(host.Facts);
            Assert.Equal(authenticated ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, facts.StatusCode);
        }
        Assert.Equal(0, host.AgentConstructions);
        host.Client.DefaultRequestHeaders.Add("X-Test-Actor", host.Actor.Id.ToString());
        await PendingGenerationProvider.GenerateAsync(host.Db, host.Entry, host.Actor.Id, host.Project.Id);
        host.Agent.Result = new() { Additions = [new(host.Entry.Content, "alice", "Person", host.Entry.Content)] };
        using var plan = await host.PostAsync(host.Plan, "{}");
        Assert.Equal(HttpStatusCode.OK, plan.StatusCode);
        var proposal = await plan.Content.ReadFromJsonAsync<JsonElement>();
        using var accepted = await host.PostAsync(host.Accept(proposal.GetProperty("proposalId").GetGuid()));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(1, host.AgentConstructions);
        Assert.Equal("Alice owns delivery.", host.Entry.Content);
    }
}
