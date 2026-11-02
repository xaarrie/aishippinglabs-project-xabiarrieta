using ProjectTimeline.EvaluationLabeler;

namespace ProjectTimeline.SearchFactsJudge;

public static class SearchJudgeConsole
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        Func<string?>? readKey = null, CancellationToken ct = default)
    {
        if (args is ["--help"])
        {
            await output.WriteLineAsync(SearchJudgeCommand.Usage);
            return 0;
        }
        try
        {
            var command = SearchJudgeCommand.Parse(args);
            // No configuration builder: the launching environment is the ONLY production key source.
            var key = (readKey ?? (() => Environment.GetEnvironmentVariable("OPENAI_API_KEY")))();
            if (string.IsNullOrWhiteSpace(key))
            {
                await error.WriteLineAsync("OPENAI_API_KEY is required in the launching process; no output was created.");
                return 2;
            }
            SearchResponseJudge? judge = null;
            try
            {
                var result = await SearchJudgeRunner.RunAsync(command, (input, snapshot, token) =>
                {
                    // The runner reserves all reports and snapshots the exact rubric before this seam.
                    judge ??= SearchResponseJudge.CreateOpenAI(key, snapshot, command.Model, TimeSpan.FromSeconds(command.TimeoutSeconds));
                    return judge.JudgeAsync(input, token);
                }, output, ct);
                return result.ExitCode;
            }
            finally { judge?.Dispose(); }
        }
        catch (LabelerException e) { await error.WriteLineAsync("Input validation failed: " + e.Code); }
        catch (SearchJudgeException e) when (e.Code is "OutputExists" or "OutputParentUnavailable" or "PromptEmpty" or "InvalidTimeout")
        { await error.WriteLineAsync("Preflight failed: " + e.Code); }
        catch (ArgumentException) { await error.WriteLineAsync("Invalid arguments.\n" + SearchJudgeCommand.Usage); }
        catch (Exception) { await error.WriteLineAsync("Input/output or execution failed; inspect any reserved reports. No exception details are logged."); }
        return 2;
    }
}
