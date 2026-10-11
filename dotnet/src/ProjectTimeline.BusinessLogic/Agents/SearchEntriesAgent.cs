using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
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
        public SearchEntriesAgent(ProjectTimelineDbContext db, Guid timelineId)
        {
            this.db = db;
            provider = new CustomOpenAIClient();
            agent = new LocalCustomAgent(
                provider
                );
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
        public async Task<AgentResponse> RunAsync(string message)
        {
            return await agent.RunAsync(message);
        }

    }
}
