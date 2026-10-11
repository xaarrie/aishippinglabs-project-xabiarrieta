using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.BusinessLogic.Agents
{
    public class GetAITools
    {
        public List<AIFunction> GetTools<T>(T instance) where T : class
        {
            var toolsInstance = instance;

            List<AIFunction> tools =
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

                        return AIFunctionFactory.Create(del);
                    })
                    .ToList();
            return tools;
        }
    }
    public class SearchTools(ProjectTimelineDbContext db)
    {
        [Description("Method that searches in the snapshots about related text")]
        public void search_snapshots()
        {

        }
        [Description("Method that searches in the entries about related text")]
        public async Task<string> search_entries(string text, Guid timelineId)
        {
            var bounded = (text ?? "").Trim(); if (bounded.Length is 0 or > 200) return "";
            var query = db.TimelineEntries.AsNoTracking().Where(x => x.TimelineId == timelineId);
            if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
                query = query.Where(x => EF.Functions.ToTsVector("simple", x.Content).Matches(EF.Functions.PlainToTsQuery("simple", bounded)));
            else query = query.Where(x => x.Content.Contains(bounded, StringComparison.OrdinalIgnoreCase));
            return string.Join("\n", await query.OrderBy(x => x.EntryNumber).ThenBy(x => x.Id).Select(x => x.Content).ToListAsync());
        }
        [Description("Method that returns snapshot content")]
        public void fetch_snapshot()
        {

        }
        [Description("Method that returns entry content")]
        public async Task<string?> fetch_entry(Guid entryId, Guid timelineId)
        {
            return await db.TimelineEntries.AsNoTracking().Where(x => x.Id == entryId && x.TimelineId == timelineId).Select(x => x.Content).SingleOrDefaultAsync();
        }
    }
}
