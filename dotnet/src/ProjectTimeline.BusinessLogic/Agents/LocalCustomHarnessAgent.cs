using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.Text;

namespace ProjectTimeline.BusinessLogic.Agents
{
    public class LocalCustomHarnessAgent : ICustomAgent
    {
      
        ICustomAgentClient provider;

        AIAgent? agent;
        public LocalCustomHarnessAgent(ICustomAgentClient _provider, string agentName, string model, string instructions, List<AITool> tools)
        {
            this.provider = _provider;
            


        }
        public void Init(string agentName, string model, string instructions, List<AITool> tools)
        {

            agent = provider.CreateHarnessAgent(model, agentName, instructions, tools);

        }
        public async Task<AgentResponse> RunAsync(string message)
        {
            if( agent != null)
                return await agent.RunAsync(message);

            throw new Exception("agent cannot be null");
        }
        public async Task<AgentResponse<T>> RunAsync<T>(string message) where T : class
        {
            if (agent != null)
            {
                return await agent.RunAsync<T>(message);

            }
            throw new Exception("agent cannot be null");
        }

    }
}
