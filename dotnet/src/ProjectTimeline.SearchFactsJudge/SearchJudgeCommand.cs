using System.Globalization;

namespace ProjectTimeline.SearchFactsJudge;

public sealed record SearchJudgeCommand(string Input, string Labels, string Output, string Model,
    string Prompt, string? ExpectedSha256, int TimeoutSeconds)
{
    public const string Usage = """
        SearchFactsJudge --input <source.jsonl> --output <new-directory>
          [--labels <annotations.csv>] [--model <id>] [--prompt <rubric.md>]
          [--expected-sha256 <dataset-sha256>] [--timeout-seconds <1..600>]
        SearchFactsJudge --help
        Defaults: sibling .labels.csv; gpt-4o-mini; copied Prompts/SearchResponseJudge.md; 60 seconds per row.
        Uses only launching-process OPENAI_API_KEY. Results are calibration, not held-out evaluation.
        """;

    public static SearchJudgeCommand Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (key is not ("--input" or "--labels" or "--output" or "--model" or "--prompt" or "--expected-sha256" or "--timeout-seconds")
                || ++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal)
                || !values.TryAdd(key, args[i])) throw new ArgumentException("InvalidArguments");
        }
        string Required(string key) => values.GetValueOrDefault(key) ?? throw new ArgumentException("InvalidArguments");
        var input = Path.GetFullPath(Required("--input"));
        var sha = values.GetValueOrDefault("--expected-sha256");
        if (sha is not null && (sha.Length != 64 || sha.Any(c => !char.IsAsciiHexDigit(c)))) throw new ArgumentException("InvalidArguments");
        var seconds = 60;
        if (values.TryGetValue("--timeout-seconds", out var raw)
            && (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out seconds) || seconds is < 1 or > 600))
            throw new ArgumentException("InvalidArguments");
        return new(input, Path.GetFullPath(values.GetValueOrDefault("--labels") ?? Path.ChangeExtension(input, ".labels.csv")),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(Required("--output"))),
            values.GetValueOrDefault("--model") ?? SearchResponseJudge.DefaultModel,
            Path.GetFullPath(values.GetValueOrDefault("--prompt") ?? Path.Combine(AppContext.BaseDirectory, "Prompts", "SearchResponseJudge.md")),
            sha?.ToLowerInvariant(), seconds);
    }
}
