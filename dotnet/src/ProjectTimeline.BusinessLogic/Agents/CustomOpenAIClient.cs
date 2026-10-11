using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System;
using System.Collections.Generic;
using System.Text;

namespace ProjectTimeline.BusinessLogic.Agents
{
    public class CustomOpenAIClient : ICustomChatClient, ICustomAgentClient
    {
        private readonly string openaikey;

        public CustomOpenAIClient()
        {
            openaikey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
            if (string.IsNullOrWhiteSpace(openaikey))
                throw new InvalidOperationException("OPENAI_API_KEY environment variable is required.");
        }

        public AIAgent CreateAgent(string model, string agentName, string instructions, List<AITool> tools)
        {
            
            var client = GetChatClient(model);
            var agent = client.AsAIAgent(
            name: agentName,
            instructions: instructions,
            tools: tools);
            return agent;
        }

        public AIAgent CreateHarnessAgent(string model, string agentName, string instructions, List<AITool> tools)
        {
            var client = new OpenAIClient(openaikey);
            return client.GetChatClient(model).AsIChatClient().AsHarnessAgent(
                new HarnessAgentOptions
                {
                    Name = agentName,
                    HarnessInstructions = "Use tools deliberately and report verified results.",
                    ChatOptions = new ChatOptions
                    {
                        Instructions = instructions,
                        Tools = tools
                    },
                  
                }
                );
        }

        public IChatClient GetChatClient(string model)
        {
            var client = new OpenAIClient(openaikey);
            return client.GetChatClient(model).AsIChatClient();
        }
    }
}
