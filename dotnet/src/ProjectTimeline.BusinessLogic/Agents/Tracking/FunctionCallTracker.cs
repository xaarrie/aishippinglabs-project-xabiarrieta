using ProjectTimeline.BusinessLogic.Agents.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace ProjectTimeline.BusinessLogic.Agents.Tracking
{
    public interface IFunctionCallTracker
    {
        List<FunctionCall> Tools { get; }
        void FunctionCallReceiver(object? sender, FunctionCallEventArgs functionCall);
    }
    public class FunctionCallTracker : IFunctionCallTracker
    {
        private readonly object sync = new();

        public FunctionCallTracker() {
            Tools = new List<FunctionCall>();
        }
        public List<FunctionCall> Tools { get; private set; }
        public void FunctionCallReceiver(object? sender, FunctionCallEventArgs functionCall)
        {
            if (functionCall?.Function is not { } incoming)
                return;
            lock (sync)
            {
                // A direct invocation has no SDK ID: merge its own progress object only,
                // never two unidentified calls or unrelated calls with the same function name.
                var existingFunction = Tools.FirstOrDefault(d => ReferenceEquals(d, incoming)
                    || (!string.IsNullOrEmpty(incoming.ID) && d.ID == incoming.ID));
                if (existingFunction is null)
                    Tools.Add(incoming);
                // A later SDK terminal-result event must not erase immediate failed-call evidence.
                else if (incoming.Executed && (existingFunction.Exception is null || incoming.Exception is not null))
                {
                    existingFunction.Executed = true;
                    existingFunction.Exception = incoming.Exception;
                    existingFunction.Result = incoming.Result;
                }
            }
        }
    }
}
