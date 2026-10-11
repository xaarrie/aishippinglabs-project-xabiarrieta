using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace ProjectTimeline.BusinessLogic.Agents
{
    public interface IConversationStorage
    {
        Task SaveConversationAsync(string agentName, string conversationId, List<ChatMessage> messages);
        Task<List<ChatMessage>> LoadConversationAsync(string agentName, string conversationId);
    }
    public interface ICustomChatClient
    {
        IChatClient GetChatClient(string model);
    }
    public interface ICustomAgentClient
    {
        AIAgent CreateAgent(string model, string agentName, string instructions, List<AITool> tools);
        AIAgent CreateHarnessAgent(string model, string agentName, string instructions, List<AITool> tools);

    }
    public interface ICustomAgent
    {
        void Init(string agentName, string model, string instructions, List<AITool> tools);
        Task<AgentResponse> RunAsync(string message);
        Task<AgentResponse<T>> RunAsync<T>(string message) where T : class;
    }
    
    
}
