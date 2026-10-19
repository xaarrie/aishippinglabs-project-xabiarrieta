using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ProjectTimeline.DataAccessLogic;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace ProjectTimeline.BusinessLogic.Agents.Tools
{
    public class GetAITools
    {
      
        public List<AITool> GetTools<T>(T instance) where T : class
        {
            var toolsInstance = instance;

            List<AITool> tools =
                toolsInstance.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => !m.IsSpecialName)
                    .Select(m =>
                    {
                        var delegateType = Expression.GetDelegateType(
                            m.GetParameters()
                             .Select(p => p.ParameterType)
                             .Concat(new[] { m.ReturnType })
                             .ToArray());

                        var del = m.CreateDelegate(delegateType, toolsInstance);

                        return  (AITool)AIFunctionFactory.Create(del);
                    })
                    .ToList();
            return tools;
        }
    }
    // Compatibility tool name; authorization scope is captured exclusively by its server constructor.
    public sealed class SearchEntries(ProjectTimelineDbContext db, TimelineFactInformationScope scope, Guid actorId, bool administrator)
    {
        [Description("Search latest valid accepted timeline facts by bounded lexical/entity terms. Never searches original entries.")]
        public Task<TimelineFactInformationEvidence> search_entries(
            [Description("Search text, at most 300 characters")] string text) =>
            new TimelineFactInformationSearch(db, scope, actorId, administrator).SearchAsync(text);
    }
    public class SearchTools()
    {
        [Description("Method that searches in the snapshots about related text")]
        public void search_snapshots()
        {

        }
        
        [Description("Method that returns snapshot content")]
        public void fetch_snapshot()
        {

        }
        
    }
}
