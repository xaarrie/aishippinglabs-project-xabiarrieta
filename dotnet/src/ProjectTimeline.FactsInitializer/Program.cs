using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.FactsInitializer;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try { return await ExecuteAsync(args, Environment.GetEnvironmentVariable, Console.Out, cancellation.Token); }
        finally { Console.CancelKeyPress -= handler; }
    }

    public static async Task<int> ExecuteAsync(string[] args, Func<string, string?> environment,
        TextWriter output, CancellationToken ct = default)
    {
        if (args is ["--help"])
        {
            await output.WriteLineAsync(InitializerCommand.Usage);
            return 0;
        }
        InitializerOptions options;
        TimelineFactRetryOptions? retry = null;
        string connection;
        try
        {
            if (args.Contains("--retry-existing", StringComparer.Ordinal))
            {
                retry = InitializerCommand.ParseRetry(args);
                options = new(retry.ActorId, retry.Apply, retry.PageSize);
            }
            else options = InitializerCommand.Parse(args);
            connection = environment("ConnectionStrings__ProjectTimeline") ?? "";
            var parsed = new NpgsqlConnectionStringBuilder(connection);
            if (string.IsNullOrWhiteSpace(parsed.Host) || string.IsNullOrWhiteSpace(parsed.Database)
                || string.IsNullOrWhiteSpace(parsed.Username)) throw new ArgumentException();
            if (options.Apply && string.IsNullOrWhiteSpace(environment("OPENAI_API_KEY"))) throw new ArgumentException();
        }
        catch (Exception)
        {
            await output.WriteLineAsync("Configuration invalid. Require --actor-id, explicit connection environment (Host/Database/Username), and OPENAI_API_KEY for --apply.");
            await output.WriteLineAsync(InitializerCommand.Usage);
            return 1;
        }

        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        try
        {
            var dbOptions = new DbContextOptionsBuilder<ProjectTimelineDbContext>().UseNpgsql(connection).Options;
            var model = environment("TimelineFacts__ExtractionModel") ?? "gpt-4o-mini";
            ITimelineFactProvider CreateProvider()
            {
                var client = new CustomOpenAIClient(environment("OPENAI_API_KEY")!);
                return new TimelineFactOpenAIProvider(new TimelineFactExtractionAgent(client, model),
                    new TimelineFactSemanticVerifierAgent(client, model), client);
            }
            await output.WriteLineAsync(options.Apply ? "APPLY: persist pending extraction candidates only. Saved means nonempty pending evidence, not active facts; activation requires explicit reconciliation acceptance elsewhere. No acceptance or embeddings here." : "DRY RUN: no extraction calls or writes.");
            void Report(EntryResult result)
            {
                counts[result.Outcome] = counts.GetValueOrDefault(result.Outcome) + 1;
                output.WriteLine($"{result.EntryId:D} {result.Outcome}" +
                    (result.DiagnosticCode is { } diagnostic ? $" Diagnostic={diagnostic}" : ""));
            }
            if (retry is not null)
                await new TimelineFactRetryRunner(() => new(dbOptions), CreateProvider, model).RunAsync(retry, Report, ct);
            else
                await new FactInitializer(() => new(dbOptions), CreateProvider).RunAsync(options, Report, ct);
            return counts.Keys.Any(x => x is not ("Saved" or "WouldSave" or "WouldRetry" or "ExistingWork" or "Empty")) ? 2 : 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await output.WriteLineAsync("Cancelled; persisted work is preserved.");
            return 130;
        }
        catch (UnauthorizedAccessException)
        {
            await output.WriteLineAsync("Actor missing or inactive.");
            return 2;
        }
        catch (Exception)
        {
            await output.WriteLineAsync("Initializer failed; inspect configuration and database privately. No exception payload is logged.");
            return 2;
        }
        finally
        {
            foreach (var (outcome, count) in counts) await output.WriteLineAsync($"{outcome}: {count}");
        }
    }
}
