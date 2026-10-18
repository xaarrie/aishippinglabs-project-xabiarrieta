using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.BusinessLogic.Agents.AgentModels;
using System.Text.Json;
using ProjectTimeline.BusinessLogic.Agents.Models;

namespace ProjectTimeline.Tests.AgentTests.Support;

/// <summary>
/// Infrastructure-only ICustomAgent seam. Captures prepared messages and consumer initialization.
/// By default stops without a response; opt-in synthetic drafts test transport behavior only.
/// Never calls a model. Scripted responses/events do not prove review quality.
/// </summary>
internal sealed class ScriptedInfrastructureAgent : ICustomAgent
{
    public string Name { get; private set; } = "";
    public string Model { get; private set; } = "";
    public string Instructions { get; private set; } = "";
    public IReadOnlyList<AITool> Tools { get; private set; } = [];
    public string? Message { get; private set; }
    public Type? ResponseType { get; private set; }
    public Func<ScriptedInfrastructureAgent, Task>? Script { get; set; }
    public Action? OnInit { get; set; }
    public int InitCalls { get; private set; }
    public int RunCalls { get; private set; }
    public List<string> Messages { get; } = [];
    // Transport testing only. This synthetic response is never a model-quality evaluation.
    public bool ReturnTransportDraft { get; set; }
    public Status TransportStatus { get; set; } = Status.Ready;
    public int DraftsReturned { get; private set; }
    private EventHandler<FunctionCallEventArgs>? functionCalled;
    public int Subscribers => functionCalled?.GetInvocationList().Length ?? 0;
    public event EventHandler<FunctionCallEventArgs>? FunctionCalled
    {
        add { functionCalled += value; }
        remove { functionCalled -= value; }
    }

    public void Init(string agentName, string model, string instructions, List<AITool> tools)
    {
        InitCalls++;
        (Name, Model, Instructions, Tools) = (agentName, model, instructions, tools.ToArray());
        OnInit?.Invoke();
    }

    public Task<AgentResponse> RunAsync(string message)
        => throw new NotSupportedException("OptimizeEntryAgent must request a typed response.");

    public async Task<AgentResponse<T>> RunAsync<T>(string message) where T : class
    {
        RunCalls++;
        Message = message;
        Messages.Add(message);
        ResponseType = typeof(T);
        if (Script is not null)
            await Script(this);
        if (!ReturnTransportDraft)
            throw new InfrastructureScriptCompletedException();
        DraftsReturned++;
        var draft = new EntryReviewResult(TransportStatus, [], new("Transport fixture", "", [], [], [], []), [], "Transport fixture only");
        return new AgentResponse<T>(new AgentResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(draft))), JsonSerializerOptions.Default);
    }

    public void Report(FunctionCall call) => functionCalled?.Invoke(this, new FunctionCallEventArgs { Function = call });
}

internal sealed class InfrastructureScriptCompletedException : Exception
{
    public InfrastructureScriptCompletedException() : base("Script completed; no model response was fabricated.") { }
}
