using System;
using System.Collections.Generic;
using System.Text;

namespace ProjectTimeline.BusinessLogic.Agents.Models
{
    public class FunctionCallEventArgs:EventArgs{
        public FunctionCall? Function { get; set; }
    }
    public class FunctionCall
    {
        public FunctionCall()
        {
            Args = new List<FunctionArg>();
            Executed = false;
        }
        public string? ID { get; set; }
        public Object? Result { get; set; }
        public string? Name { get; set; }
        public List<FunctionArg> Args { get; set; }
        public bool Executed { get; set; }
        public Exception? Exception { get; set; }

    }
    public class FunctionArg
    {
        public string? Name { get; set; }
        public object? Value { get; set; }
    }
}
