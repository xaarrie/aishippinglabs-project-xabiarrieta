using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.Text;

namespace ProjectTimeline.BusinessLogic.Agents
{
    public class LocalCustomAgent : ICustomAgent
    {
        
        ICustomAgentClient provider;

        AIAgent? agent;
        public LocalCustomAgent(ICustomAgentClient _provider)
        {
            this.provider = _provider;
            
        }
        public void Init(string agentName, string model, string instructions, List<AITool> tools)
        {
            
            agent = provider.CreateAgent(model, agentName, instructions, tools);

        }
        public Task<AgentResponse> RunAsync(string message)
        {
            if (agent == null)
            {
                throw new Exception("agent cannot be null");
            }
            return agent.RunAsync(message);
        }
        public Task<AgentResponse<T>> RunAsync<T>(string message) where T : class
        {
            if (agent == null)
            {
                throw new Exception("agent cannot be null");
            }
            return agent.RunAsync<T>(message);
        }

    }
}
