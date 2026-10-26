using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.BusinessLogic.Agents.Models;
using ProjectTimeline.Tests.AgentTests.Support;
using Xunit;

namespace ProjectTimeline.Tests.AgentTests;

[Collection(IsolatedAgentCollection.Name)]
public sealed class CustomOpenAIClientIsolationTests
{
    [Fact]
    public void Factory_supports_explicit_isolation_and_credential_free_injected_transport()
    {
        Assert.Contains(typeof(CustomOpenAIClient).GetConstructors(), constructor =>
            constructor.GetParameters().Select(parameter => parameter.ParameterType)
                .SequenceEqual([typeof(IChatClient), typeof(bool)]));
        Assert.Contains(typeof(CustomOpenAIClient).GetConstructors(), constructor =>
            constructor.GetParameters().Select(parameter => parameter.ParameterType)
                .SequenceEqual([typeof(bool)]));
    }

    [Fact]
    public void Null_injected_transport_is_rejected_without_consulting_credentials()
        => Assert.Throws<ArgumentNullException>(() => new CustomOpenAIClient((IChatClient)null!, isolateHarnessFacilities: true));

    [Fact]
    public async Task All_factories_use_injected_transport_and_isolated_harness_has_only_explicit_tools()
    {
        using var transport = new HarnessScriptedTransport([]);
        var provider = new CustomOpenAIClient(transport, isolateHarnessFacilities: true);
        Assert.Same(transport, provider.GetChatClient("not-a-provider-model"));
        await provider.CreateAgent("not-a-provider-model", "plain", "plain instructions", []).RunAsync("plain wire control");
        Assert.Equal(1, transport.Requests);

        using var harnessTransport = new HarnessScriptedTransport([]);
        var harnessProvider = new CustomOpenAIClient(harnessTransport, isolateHarnessFacilities: true);
        var tool = AIFunctionFactory.Create(() => "synthetic", "explicit_context");
        await harnessProvider.CreateHarnessAgent("not-a-provider-model", "isolated", "isolated instructions", [tool])
            .RunAsync("harness wire control");
        Assert.Equal(1, harnessTransport.Requests);
        Assert.Equal("plain wire control", Assert.Single(Assert.Single(transport.Messages), message => message.Role == ChatRole.User).Text);
        Assert.Equal("harness wire control", Assert.Single(Assert.Single(harnessTransport.Messages), message => message.Role == ChatRole.User).Text);
        var options = Assert.Single(harnessTransport.Options);
        Assert.Equal(["explicit_context"], options.Tools!.OfType<AIFunction>().Select(function => function.Name));
        Assert.Single(options.Tools!); // No memory, skills, todo, mode, file access or hosted web tools.
        Assert.DoesNotContain("agent-file-memory", options.Instructions ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SKILL.md", options.Instructions ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Local_harness_without_project_tools_does_not_advertise_web_search()
    {
        using var transport = new HarnessScriptedTransport([]);
        var agent = new LocalCustomHarnessAgent(new CustomOpenAIClient(transport, isolateHarnessFacilities: true));
        agent.Init("offline", "not-a-provider-model", "Offline wrapper control.", []);

        var response = await agent.RunAsync("no tools control");

        Assert.Equal(HarnessScriptedTransport.WireResponse, response.Text);
        Assert.Equal(1, transport.Requests);
        var options = Assert.Single(transport.Options);
        Assert.DoesNotContain(options.Tools ?? [], tool => tool is HostedWebSearchTool);
        Assert.Empty(options.Tools ?? []);
    }

    [Theory]
    [InlineData("text", false)]
    [InlineData("text", true)]
    [InlineData("text-cancellable", false)]
    [InlineData("text-cancellable", true)]
    [InlineData("typed", false)]
    [InlineData("typed", true)]
    [InlineData("typed-cancellable", false)]
    [InlineData("typed-cancellable", true)]
    [InlineData("structured", false)]
    [InlineData("structured", true)]
    public async Task Local_harness_overloads_exclude_web_search_and_preserve_executed_project_tool_results(
        string runKind, bool explicitTelemetryOptions)
    {
        const string toolName = "explicit_project_context";
        const string callId = "project-call";
        const string projectId = "synthetic-project";
        const string expectedResult = "Synthetic project evidence, not web content.";
        var executions = 0;
        var tool = AIFunctionFactory.Create((string requestedProject) =>
        {
            Assert.Equal(projectId, requestedProject);
            executions++;
            return expectedResult;
        }, toolName);
        using var transport = new HarnessScriptedTransport([
            [new FunctionCallContent(callId, toolName, new Dictionary<string, object?> { ["requestedProject"] = projectId })]
        ]);
        // Non-isolated SDK defaults create file memory and discover local skills. Keep this control filesystem-free.
        var provider = explicitTelemetryOptions
            ? new CustomOpenAIClient(transport, isolateHarnessFacilities: true, telemetryOptions: new(false))
            : new CustomOpenAIClient(transport, isolateHarnessFacilities: true);
        var agent = new LocalCustomHarnessAgent(provider);
        agent.Init("offline", "not-a-provider-model", "Use only the supplied project function.", [tool]);
        var notifications = new List<FunctionCall>();
        agent.FunctionCalled += (_, args) => notifications.Add(Assert.IsType<FunctionCall>(args.Function));
        using var cancellation = new CancellationTokenSource();

        var response = runKind switch
        {
            "text" => await agent.RunAsync("project control"),
            "text-cancellable" => await agent.RunAsync("project control", cancellation.Token),
            "typed" => await agent.RunAsync<WireResult>("project control"),
            "typed-cancellable" => await agent.RunAsync<WireResult>("project control", cancellation.Token),
            "structured" => await agent.RunAsync<WireResult>("project control", new StructuredAgentRunOptions
            {
                ChatOptions = new ChatOptions { ResponseFormat = ChatResponseFormat.Json },
                SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web),
                ValidateResponse = result => Assert.NotEmpty(result.Messages),
                InvalidResult = () => new InvalidDataException("Invalid offline structured result.")
            }, cancellation.Token),
            _ => throw new ArgumentOutOfRangeException(nameof(runKind))
        };

        Assert.Equal(HarnessScriptedTransport.WireResponse, response.Text);
        if (runKind is "typed" or "typed-cancellable" or "structured")
            Assert.Equal(JsonSerializer.Deserialize<WireResult>(HarnessScriptedTransport.WireResponse)!.Message,
                Assert.IsType<AgentResponse<WireResult>>(response).Result.Message);
        Assert.Equal(1, executions);
        Assert.Equal(2, transport.Requests); // One scripted function call, then the final answer.
        Assert.Equal(2, transport.Options.Count);
        Assert.All(transport.Options, options =>
        {
            Assert.NotNull(options.Tools);
            Assert.DoesNotContain(options.Tools, supplied => supplied is HostedWebSearchTool);
            Assert.Equal(toolName, Assert.IsAssignableFrom<AIFunction>(Assert.Single(options.Tools)).Name);
        });
        Assert.Empty(transport.Messages[0].SelectMany(message => message.Contents).OfType<FunctionResultContent>());
        var result = Assert.Single(transport.Messages[1].SelectMany(message => message.Contents).OfType<FunctionResultContent>());
        Assert.Equal(callId, result.CallId);
        Assert.Null(result.Exception);
        Assert.Equal(JsonSerializer.Serialize(expectedResult), JsonSerializer.Serialize(result.Result));
        var notification = Assert.Single(notifications, item => item.Executed);
        Assert.Equal(2, notifications.Count);
        Assert.Equal(toolName, Assert.Single(notifications, item => !item.Executed).Name);
        Assert.Equal(callId, notification.ID);
        Assert.Null(notification.Exception);
        Assert.Equal(JsonSerializer.Serialize(expectedResult), JsonSerializer.Serialize(notification.Result));
    }

    public sealed class WireResult
    {
        [JsonPropertyName("message")]
        public string Message { get; set; } = "";
    }
}
