using System.Text.Json;
using Npgsql;

namespace ProjectTimeline.SearchFactsEvaluator;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try { return await ExecuteAsync(args, Environment.GetEnvironmentVariable, Console.Out, ct: cancellation.Token); }
        finally { Console.CancelKeyPress -= handler; }
    }

    public static async Task<int> ExecuteAsync(string[] args, Func<string, string?> environment, TextWriter output,
        Func<string, string?, IEvaluationBackend>? backendFactory = null, CancellationToken ct = default)
    {
        if (args is ["--help"]) { await output.WriteLineAsync(EvaluatorCommand.Usage); return 0; }
        EvaluatorCommand command;
        ScenarioInput? input = null;
        string connection;
        string? key = null;
        try
        {
            command = EvaluatorCommand.Parse(args);
            if (!command.ListScopes)
            {
                input = ScenarioInput.Parse(await File.ReadAllBytesAsync(command.Input!, ct), command.Input!);
                EvaluationRunner.ValidateOutput(input.Path, command.Output!);
            }
            connection = environment("ConnectionStrings__ProjectTimeline") ?? "";
            var parsed = new NpgsqlConnectionStringBuilder(connection);
            if (string.IsNullOrWhiteSpace(parsed.Host) || string.IsNullOrWhiteSpace(parsed.Database)
                || string.IsNullOrWhiteSpace(parsed.Username)) throw new ArgumentException();
            if (!command.Preflight && !command.ListScopes)
            {
                key = environment("OPENAI_API_KEY");
                if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return 130; }
        catch (Exception)
        {
            await output.WriteLineAsync("Invalid input, output paths, arguments or configuration. Require UTF-8 CSV, new adjacent output paths, explicit scope and connection environment; execution also needs OPENAI_API_KEY.");
            await output.WriteLineAsync(EvaluatorCommand.Usage);
            return 1;
        }
        try
        {
            var backend = backendFactory?.Invoke(connection, key) ?? new ProductionBackend(connection, key);
            if (command.ListScopes)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(await backend.ListScopesAsync(ct), EvaluationRunner.Json));
                return 0;
            }
            var scope = await backend.InspectAsync(command, ct);
            if (command.Preflight)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(new { input!.Path, input.Sha256,
                    ScenarioCount = input.Scenarios.Count, Scope = scope, Model = "gpt-4o-mini", Mode = "Preflight" }, EvaluationRunner.Json));
                return 0;
            }
            var manifest = await EvaluationRunner.RunAsync(input!, scope, command.Output!, () => backend.CreateSession(command), ct);
            await output.WriteLineAsync($"{manifest.Status}: persisted={manifest.Persisted}, returned={manifest.Returned}, failed={manifest.Failed}, cancelled={manifest.Cancelled}, unstarted={manifest.Unstarted}.");
            return ct.IsCancellationRequested ? 130 : manifest.Status == "Completed" && manifest.Failed == 0 ? 0 : 2;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return 130; }
        catch (Exception ex)
        {
            await output.WriteLineAsync($"Stopped: {EvaluationRunner.Classify(ex).Code}. Inspect configuration/database privately; existing local artifacts are preserved. No exception details logged.");
            return 2;
        }
    }
}
