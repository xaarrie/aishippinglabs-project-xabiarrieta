using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.BusinessLogic.Agents.Models;

namespace ProjectTimeline.SearchFactsEvaluator;

public sealed class CapturingAgent(ICustomAgent inner) : ICustomAgent
{
    public IReadOnlyList<CapturedMessage> Messages { get; private set; } = [];
    public AgentFailureDiagnostic? FailureDiagnostic { get; private set; }
    public event EventHandler<FunctionCallEventArgs>? FunctionCalled
    { add => inner.FunctionCalled += value; remove => inner.FunctionCalled -= value; }
    public void Init(string agentName, string model, string instructions, List<AITool> tools)
    {
        FailureDiagnostic = null;
        try { inner.Init(agentName, model, instructions, tools); }
        catch (Exception error) { FailureDiagnostic = AgentFailureDiagnostic.FromException(error); throw; }
    }
    public Task<AgentResponse> RunAsync(string message) => RunAsync(message, CancellationToken.None);
    public Task<AgentResponse<T>> RunAsync<T>(string message) where T : class => RunAsync<T>(message, CancellationToken.None);
    public async Task<AgentResponse> RunAsync(string message, CancellationToken ct)
    {
        var response = await ObserveAsync(() => inner.RunAsync(message, ct));
        Capture(response.Messages);
        return response;
    }
    public async Task<AgentResponse<T>> RunAsync<T>(string message, CancellationToken ct) where T : class
    {
        var response = await ObserveAsync(() => inner.RunAsync<T>(message, ct));
        // Do not read Result: invalid structured synthesis must not erase already returned evidence.
        Capture(response.Messages);
        return response;
    }

    public async Task<AgentResponse<T>> RunAsync<T>(string message, StructuredAgentRunOptions options, CancellationToken ct) where T : class
    {
        var response = await ObserveAsync(() => inner.RunAsync<T>(message, options, ct));
        // The inner structured validator may reject before a response reaches this decorator.
        // Capture returned messages before touching any lazy Result; retain prior stage messages.
        Capture(response.Messages);
        return response;
    }

    private async Task<T> ObserveAsync<T>(Func<Task<T>> operation)
    {
        FailureDiagnostic = null;
        try { return await operation(); }
        catch (Exception error) { FailureDiagnostic = AgentFailureDiagnostic.FromException(error); throw; }
    }

    private void Capture(IList<ChatMessage> messages) => Messages = Messages.Concat(messages.Select(message => new CapturedMessage(
        message.Role.Value, message.Contents.Select(content => content switch
        {
            TextContent text => new CapturedContent("text", Text: text.Text),
            FunctionCallContent call => new CapturedContent("functionCall", CallId: call.CallId, Name: call.Name,
                Payload: JsonSerializer.SerializeToElement(call.Arguments?.ToDictionary(x => x.Key, x => SafeValue(x.Value)))),
            FunctionResultContent result => new CapturedContent("functionResult", CallId: result.CallId,
                Payload: result.Exception is null ? JsonSerializer.SerializeToElement(SafeValue(result.Result)) : null,
                Failed: result.Exception is not null),
            _ => new CapturedContent("unsupported")
        }).ToArray()))).ToArray();

    // Only JSON-like payloads are copied. Never serialize SDK raw representations, headers or exceptions.
    private static object? SafeValue(object? value) => value switch
    {
        JsonElement json => json.Clone(),
        null or string or bool or byte or short or int or long or float or double or decimal or Guid => value,
        Array array => array.Cast<object?>().Select(SafeValue).ToArray(),
        _ => null
    };
}
