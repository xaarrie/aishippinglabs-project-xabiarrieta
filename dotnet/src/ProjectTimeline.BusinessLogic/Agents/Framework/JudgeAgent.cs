using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ProjectTimeline.BusinessLogic.Agents.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;

namespace ProjectTimeline.BusinessLogic.Agents.Framework
{
    public class JudgeAgent:IJudgeAgent
    {
        public string JudgeInstructions { get; set; }
        public JudgeAgent(ICustomAgentClient _provider) 
        {
            Criteria = "";
            AgentOutput = "";
            ToollCalls = new List<string>();
            this.provider = _provider;
        }
        ICustomAgentClient provider;

        AIAgent? agent;
        public string Criteria { get; set; }
        public string AgentOutput { get; set; }
        public List<string> ToollCalls { get; set; } 
        public string JudgeUserPrompt
        {
            get { return $@"
            Evaluate the agent's performance based on the following criteria:
            <CRITERIA>
            {this.Criteria}
            </CRITERIA>

            The agent's final output was:
            <AGENT_OUTPUT>
            {this.AgentOutput}
            </AGENT_OUTPUT>

            Tool calls:
            <TOOL_CALLS>
            {string.Join(Environment.NewLine, ToollCalls)}
            </TOOL_CALLS>
        "; }
        }

        public void Init(string agentName, string model, string instructions)
        {
            this.JudgeInstructions = instructions;
            agent = provider.CreateAgent(model, agentName, this.JudgeInstructions, new List<AITool> { });
        }

        public async Task<AgentResponse<JudgeFeedback>> RunAsync(string prevAgentOutput, string criteria, List<FunctionCall> functions)
        {
            this.Criteria = criteria;
            
            this.AgentOutput = prevAgentOutput;
            this.ToollCalls = new List<string>();
            if (functions != null)
            {
                foreach (var f in functions)
                {
                    StringBuilder fs = new StringBuilder($"{f.Name}(");
                    fs.Append(string.Join(",", (f.Args ?? new List<FunctionArg>())
                        .Select(a => $"{a.Name}={a.Value?.ToString() ?? "null"}")));
                    fs.Append(")");
                    ToollCalls.Add(fs.ToString());
                }

            }
            if (agent == null)
            {
                throw new Exception("agent cannot be null");
            }
            var response = await agent.RunAsync<JudgeFeedback>(JudgeUserPrompt);
            return response;
        }
    }
    [Description("Evaluation of a single test requirement or behavioral rule.")]
    public class JudgeCriterion
    {
        [Description("The specific requirement or rule that the agent is being evaluated against.")]
        public string CriterionDescription { get; set; }
        [Description("Indicates whether the agent's response and actions successfully satisfied this requirement.")]
        public bool Passed { get; set; }
        [Description("A clear explanation of why the agent passed or failed, referencing specific evidence from the agent's output or tool calls.")]
        public string Judgement { get; set; }
    }
    [Description("The complete evaluation report from the judge agent, summarizing performance across all criteria.")]
    public class JudgeFeedback
    {
        [Description("A collection of individual evaluations for each performance requirement provided in the test.")]
        public List<JudgeCriterion> Criteria { get; set; } = new List<JudgeCriterion>();
        [Description("A holistic summary of the agent's performance, providing overall context and identifying key strengths or failures.")]
        public string Feedback { get; set; }
    }
}
