namespace ProjectTimeline.EvaluationLabeler;

public static class Program
{
    public const string Help = """
        Local evaluation labeler (.NET 10)
        Usage: dotnet ProjectTimeline.EvaluationLabeler.dll --input <results.jsonl> [--labels <labels.csv>] [--port <1024-65535>]
        Defaults: --port 5198; labels = Path.ChangeExtension(input, ".labels.csv").
        Open http://localhost:5198 (or the selected port). Only loopback connections are accepted.
        Good/Bad clicks save immediately; Bad starts as uncategorized. No API key or database is required.
        Existing labels are validated and resumed; stop the other writer before using the same CSV.
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args is ["--help"] or ["-h"]) { Console.WriteLine(Help); return 0; }
        try
        {
            var options = LabelerOptions.Parse(args);
            await using var app = LabelerApp.Create(options);
            Console.WriteLine($"Evaluation labeler: http://localhost:{options.Port} (Ctrl+C to stop)");
            await app.RunAsync();
            return 0;
        }
        catch (LabelerException ex)
        {
            Console.Error.WriteLine($"Cannot proceed: {ex.Code}" + (ex.Line > 0 ? $" at line {ex.Line}." : ".") + " No labels were changed. Use --help for usage.");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine("Cannot open input, labels, or local listener. Check paths, permissions and port. No input data was changed.");
            return 1;
        }
    }
}
