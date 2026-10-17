using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.BusinessLogic.Agents.Tools;
using ProjectTimeline.BusinessLogic.Agents.Tracking;
using ProjectTimeline.DataAccessLogic;
using System;
using System.Collections.Generic;
using System.Text;

namespace ProjectTimeline.BusinessLogic.Agents
{
    public class SearchEntriesAgent
    {
        ICustomAgent agent;
        ICustomAgentClient provider;
        ProjectTimelineDbContext db;
        IFunctionCallTracker? callTracker;
        public SearchEntriesAgent(ProjectTimelineDbContext db, Guid timelineId, IFunctionCallTracker? callTracker = null)
        {
            this.callTracker = callTracker;
            this.db = db;
            provider = new CustomOpenAIClient();
            agent = new LocalCustomAgent(
                provider
                );
            if(this.callTracker != null)
            {
                agent.FunctionCalled += this.callTracker.FunctionCallReceiver;
            }
            SearchEntries sentries = new SearchEntries(db, timelineId);
            string instructions = @$"
            You are a document assistant to try to help searching throught entries of a conversation.
            You must perform the following steps:
            1) search entries related to user prompt to read information about what is asking
            2) sintesize the content of the related entries to answer the user question 
            ";

            var tools = new GetAITools().GetTools(sentries).ToList();
            agent.Init("searchEntries", "gpt-4o-mini", instructions, tools);
        }

        private void Agent_FunctionCalled(object? sender, Models.FunctionCallEventArgs e)
        {
            throw new NotImplementedException();
        }

        public async Task<AgentResponse> RunAsync(string message)
        {
            return await agent.RunAsync(message);
        }

    }
}
