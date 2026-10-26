using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.DataAccessLogic;
using ProjectTimeline.UI.WebApi;
using Xunit;
using static ProjectTimeline.Tests.LogfireTelemetryTests;
using static ProjectTimeline.Tests.NativeAgentIntegrationTests;

namespace ProjectTimeline.Tests;

[Collection(AgentTelemetryCollection.Name)]
public sealed class AgentTraceContentTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public void Hosting_shares_one_immutable_policy_with_extraction_and_verification_without_enabling_export(bool? capture)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logfire:CaptureAgentContent"] = capture?.ToString(),
            ["OPENAI_API_KEY"] = "offline-placeholder-not-a-credential"
        }).Build();
        var services = new ServiceCollection();
        services.AddTimelineFacts(config);
        config["Logfire:CaptureAgentContent"] = (!(capture ?? true)).ToString();
        services.AddLogfireTelemetry(config); // Later config changes cannot split factories from export.
        using var sp = services.BuildServiceProvider();
        var policy = sp.GetRequiredService<AgentTelemetryOptions>();
        Assert.Equal(capture ?? true, policy.CaptureAgentContent);
        Assert.Single(services, registration => registration.ServiceType == typeof(AgentTelemetryOptions));
        Assert.Null(sp.GetService<TracerProvider>());
        using var scope = sp.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<ITimelineFactProvider>();
        var client = Assert.Single(Fields(provider).OfType<CustomOpenAIClient>());
        Assert.Same(policy, Assert.Single(Fields(client).OfType<AgentTelemetryOptions>()));
        foreach (var agent in Fields(provider).Where(value => value is TimelineFactExtractionAgent or TimelineFactSemanticVerifierAgent))
            Assert.Same(client, Assert.Single(Fields(agent!).OfType<CustomOpenAIClient>()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Search_application_propagates_host_policy_through_real_harness_tool_loop(bool? capture)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var role = new UserRole { RoleName = "User" };
        f.Actor.UserRole = role;
        f.Actor.IDUserRole = role.Id;
        f.Db.Add(role);
        await f.Db.SaveChangesAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        using var wire = await Wire.Start(capture: capture);
        var policy = wire.Services.GetRequiredService<AgentTelemetryOptions>();
        var chat = new Chat(turn => new ChatResponse(new ChatMessage(ChatRole.Assistant, turn == 1
            ? [new FunctionCallContent("SEARCH_CALL_ID", "search_facts", new Dictionary<string, object?> { ["query"] = "alice" })]
            : [new TextContent(JsonSerializer.Serialize(new { Content = "SYNTHESIS_SENTINEL", CitedFactIds = new[] { fact.Id }, CitedContextEvidenceIds = Array.Empty<Guid>() }))]))
            { ModelId = "gpt-4o-mini", Usage = new() { InputTokenCount = 100, OutputTokenCount = 20 } });
        var app = new TimelineFactInformationApplication(f.Db, chatClient: chat, telemetryOptions: policy);
        var session = AgentCostSession.Start();
        using (session)
        {
            var result = await app.SearchAsync(f.Timeline.Id, f.Actor.Id, "QUESTION_CAPTURE_SENTINEL");
            Assert.Equal("SYNTHESIS_SENTINEL", result.Content);
            Assert.Equal(2, result.Research!.ModelCalls);
            Assert.Equal(1, result.Research.ToolCalls);
        }
        Assert.Equal(2, chat.Calls);
        Assert.Equal(2, session.GetSnapshot().Totals.CallCount);
        var spans = wire.Flush();
        Assert.Equal(6, spans.Length); // Session + wrapper + invocation + two chats + tool.
        var payload = string.Join("\n", wire.Transport.Requests.Select(request => Encoding.UTF8.GetString(request.Body)));
        foreach (var expected in new[] { "QUESTION_CAPTURE_SENTINEL", "SYNTHESIS_SENTINEL", "SEARCH_CALL_ID", "Alice owns delivery." })
            if (capture != false) Assert.Contains(expected, payload);
            else Assert.DoesNotContain(expected, payload);
        Assert.Single(spans, span => Text(span, 5) == "execute_tool");
    }

    [Fact]
    public void Production_search_and_review_composition_passes_the_host_policy_without_starting_database_or_reading_credentials()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "dotnet", "ProjectTimeline.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var sourceRoot = Path.Combine(root.FullName, "dotnet", "src", "ProjectTimeline.UI.WebApi");
        var program = File.ReadAllText(Path.Combine(sourceRoot, "Program.cs"));
        Assert.Contains("telemetryOptions: sp.GetRequiredService<ProjectTimeline.BusinessLogic.Agents.Framework.AgentTelemetryOptions>()", program);
        var hosting = File.ReadAllText(Path.Combine(sourceRoot, "TimelineFactHosting.cs"));
        Assert.Contains("new LocalCustomHarnessAgent(new CustomOpenAIClient(isolateHarnessFacilities: true,", hosting);
        Assert.Equal(2, hosting.Split("telemetryOptions: sp.GetRequiredService<AgentTelemetryOptions>()").Length - 1);
        Assert.Contains("app.MapTimelineFactInformation();", program);
    }

    private static IEnumerable<object?> Fields(object instance) => instance.GetType()
        .GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Select(field => field.GetValue(instance));
}
