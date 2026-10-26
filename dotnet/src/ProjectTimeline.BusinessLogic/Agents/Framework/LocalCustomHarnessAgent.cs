using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ProjectTimeline.BusinessLogic.Agents.Models;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace ProjectTimeline.BusinessLogic.Agents.Framework
{
    public class LocalCustomHarnessAgent : ICustomAgent
    {
        public event EventHandler<FunctionCallEventArgs>? FunctionCalled;
        ICustomAgentClient provider;

        AIAgent? agent;
        private string? telemetryAgentName;
        private string? telemetryModel;
        public LocalCustomHarnessAgent(ICustomAgentClient _provider)
        {
            this.provider = _provider;



        }
        public void Init(string agentName, string model, string instructions, List<AITool> tools)
        {

            agent = provider.CreateHarnessAgent(model, agentName, instructions, tools);
            telemetryAgentName = agentName;
            telemetryModel = model;

        }
        public Task<AgentResponse> RunAsync(string message) => RunAsync(message, CancellationToken.None);

        public Task<AgentResponse> RunAsync(string message, CancellationToken ct) => AgentTelemetry.RunAsync(() =>
            AgentExecutionBudget.RunAsync(async token =>
        {
            if (agent != null)
            {
                token.ThrowIfCancellationRequested();
                var response = await agent.RunAsync(message, cancellationToken: token);
                token.ThrowIfCancellationRequested();
                AnalyzeContent(response.Messages);
                return response;
            }

            throw new Exception("agent cannot be null");
        }, ct), telemetryAgentName, telemetryModel, harness: true, runKind: "text");

        public Task<AgentResponse<T>> RunAsync<T>(string message) where T : class => RunAsync<T>(message, CancellationToken.None);

        public Task<AgentResponse<T>> RunAsync<T>(string message, CancellationToken ct) where T : class => AgentTelemetry.RunAsync(() =>
            AgentExecutionBudget.RunAsync(async token =>
        {
            if (agent != null)
            {
                token.ThrowIfCancellationRequested();
                var response = await agent.RunAsync<T>(message, cancellationToken: token);
                token.ThrowIfCancellationRequested();
                AnalyzeContent(response.Messages);
                return response;

            }
            throw new Exception("agent cannot be null");
        }, ct), telemetryAgentName, telemetryModel, harness: true, runKind: "typed");

        // Explicit opt-in; existing text and inferred typed runs retain their current behavior.
        public Task<AgentResponse<T>> RunAsync<T>(string message, StructuredAgentRunOptions options, CancellationToken ct) where T : class
            => AgentTelemetry.RunAsync(() => AgentExecutionBudget.RunAsync(async token =>
        {
            if (agent == null) throw new Exception("agent cannot be null");
            var runOptions = new ChatClientAgentRunOptions(options.ChatOptions)
            {
                ChatClientFactory = inner => new StructuredResponseClient(inner, options.ValidateResponse)
            };
            var response = await agent.RunAsync(message, options: runOptions, cancellationToken: token);
            token.ThrowIfCancellationRequested();
            var serializer = new JsonSerializerOptions(options.SerializerOptions);
            serializer.MakeReadOnly(populateMissingResolver: true);
            var typed = new AgentResponse<T>(response, serializer);
            try { _ = typed.Result ?? throw new JsonException(); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
            { throw options.InvalidResult(); }
            AnalyzeContent(response.Messages);
            return typed;
        }, ct), telemetryAgentName, telemetryModel, harness: true, runKind: "structured");

        private sealed class StructuredResponseClient(IChatClient inner, Action<ChatResponse> validate) : DelegatingChatClient(inner)
        {
            public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                var response = await base.GetResponseAsync(messages, options, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                validate(response);
                return response;
            }
        }

        protected void AnalyzeContent(IList<ChatMessage> messages)
        {
            foreach (var item in messages)
            {
                foreach (var c in item.Contents)
                {
                    if (c is FunctionCallContent callContent)
                    {
                        if (this.FunctionCalled != null)
                        {
                            List<FunctionArg> args = new List<FunctionArg>();
                            if (callContent.Arguments != null)
                            {
                                foreach (var a in callContent.Arguments)
                                {
                                    FunctionArg arg = new FunctionArg()
                                    {
                                        Name = a.Key,
                                        Value = a.Value
                                    };
                                    args.Add(arg);
                                }
                            }
                            FunctionCallEventArgs ev = new FunctionCallEventArgs()
                            {

                                Function = new FunctionCall
                                {
                                    ID = callContent.CallId,
                                    Name = callContent.Name,
                                    Executed = false,
                                    Args = args

                                }
                            };
                            FunctionCalled(this, ev);
                        }
                    }
                    else if (c is FunctionResultContent resultContent)
                    {
                        if (this.FunctionCalled != null)
                        {
                            FunctionCallEventArgs ev = new FunctionCallEventArgs()
                            {
                                Function = new FunctionCall
                                {
                                    ID = resultContent.CallId,
                                    Executed = true,
                                    Result = resultContent.Result,
                                    Exception = resultContent.Exception
                                }
                            };
                            FunctionCalled(this, ev);
                        }
                    }
                }
            }

        }
    }
}
