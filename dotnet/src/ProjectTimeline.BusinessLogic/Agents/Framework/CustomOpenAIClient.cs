using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System;
using System.Collections.Generic;
using System.Text;

namespace ProjectTimeline.BusinessLogic.Agents.Framework
{
    public class CustomOpenAIClient : ICustomChatClient, ICustomAgentClient
    {
        private readonly string? openaikey;
        private readonly IChatClient? injectedClient;
        private readonly bool isolateHarnessFacilities;

        public CustomOpenAIClient() : this(isolateHarnessFacilities: false) { }

        // Opt-in only: existing callers retain the SDK's local facilities and key validation.
        public CustomOpenAIClient(bool isolateHarnessFacilities)
        {
            this.isolateHarnessFacilities = isolateHarnessFacilities;
            openaikey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
            if (string.IsNullOrWhiteSpace(openaikey))
                throw new InvalidOperationException("OPENAI_API_KEY environment variable is required.");
        }

        // Infrastructure seam: all factories use this transport; no environment credentials needed.
        public CustomOpenAIClient(IChatClient chatClient, bool isolateHarnessFacilities = false)
        {
            injectedClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
            this.isolateHarnessFacilities = isolateHarnessFacilities;
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
            return GetChatClient(model).AsHarnessAgent(
                new HarnessAgentOptions
                {
                    Name = agentName,
                    HarnessInstructions = "Use tools deliberately and report verified results.",
                    ChatOptions = new ChatOptions
                    {
                        Instructions = instructions,
                        Tools = tools,
                       
                    },
                    DisableWebSearch = true,
                    DisableFileMemory = isolateHarnessFacilities,
                    DisableAgentSkillsProvider = isolateHarnessFacilities,
                    DisableTodoProvider = isolateHarnessFacilities,
                    DisableAgentModeProvider = isolateHarnessFacilities,
                    // File access and background agents remain unconfigured (SDK opt-in).
                  
                }
                );
        }

        public IChatClient GetChatClient(string model)
        {
            if (injectedClient is not null)
                return injectedClient;
            var client = new OpenAIClient(openaikey!);
            return client.GetChatClient(model).AsIChatClient();
        }
    }
}
