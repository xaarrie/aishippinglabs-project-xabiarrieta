using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

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
    public class SearchTools
    {
        [Description("Method that searches in the snapshots about related text")]
        public void search_snapshots()
        {

        }
        [Description("Method that searches in the entries about related text")]
        public void search_entries()
        {

        }
        [Description("Method that returns snapshot content")]
        public void fetch_snapshot()
        {

        }
        [Description("Method that returns entry content")]
        public void fetch_entry()
        {

        }
    }
}
