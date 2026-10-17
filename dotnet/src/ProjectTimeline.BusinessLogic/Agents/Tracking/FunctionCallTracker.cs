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
    public class FunctionCallTracker
    {
        public FunctionCallTracker() {
            Tools = new List<FunctionCall>();
        }
        public List<FunctionCall> Tools { get; private set; }
        public void FunctionCallReceiver(object? sender, FunctionCallEventArgs functionCall)
        {
            var existingFunction = Tools.FirstOrDefault(d => d.ID == functionCall?.Function?.ID);
            if (existingFunction != null)
            {
                if (true == functionCall?.Function?.Executed)
                {
                    existingFunction.Executed = functionCall.Function.Executed;
                    existingFunction.Exception = functionCall.Function.Exception;
                    existingFunction.Result = functionCall.Function.Result;
                }
            }
            else
            {
                if (functionCall != null && functionCall.Function != null)
                {
                    Tools.Add(functionCall.Function);
                }
            }
        }
    }
}
