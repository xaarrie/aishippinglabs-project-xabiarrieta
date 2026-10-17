using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ProjectTimeline.BusinessLogic.Agents.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace ProjectTimeline.BusinessLogic.Agents.Framework
{
    public class LocalCustomHarnessAgent : ICustomAgent
    {
        public event EventHandler<FunctionCallEventArgs>? FunctionCalled;
        ICustomAgentClient provider;

        AIAgent? agent;
        public LocalCustomHarnessAgent(ICustomAgentClient _provider)
        {
            this.provider = _provider;



        }
        public void Init(string agentName, string model, string instructions, List<AITool> tools)
        {

            agent = provider.CreateHarnessAgent(model, agentName, instructions, tools);

        }
        public async Task<AgentResponse> RunAsync(string message)
        {
            if (agent != null)
            {
                var response = await agent.RunAsync(message);
                AnalyzeContent(response.Messages);
                return response;
            }

            throw new Exception("agent cannot be null");
        }
        public async Task<AgentResponse<T>> RunAsync<T>(string message) where T : class
        {
            if (agent != null)
            {
                var response = await agent.RunAsync<T>(message);
                AnalyzeContent(response.Messages);
                return response;

            }
            throw new Exception("agent cannot be null");
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
