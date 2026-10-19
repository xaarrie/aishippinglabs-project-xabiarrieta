using Microsoft.Extensions.AI;
using ProjectTimeline.BusinessLogic.Agents.Framework;
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
}
