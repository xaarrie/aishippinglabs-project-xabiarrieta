namespace ProjectTimeline.SearchFactsEvaluator;

public sealed record EvaluatorCommand(string? Input, string? Output, Guid ActorId, Guid TimelineId,
    Guid ExpectedProjectId, bool Preflight, bool ListScopes)
{
    public const string Usage = "SearchFactsEvaluator --list-scopes | --input <csv> --actor-id <guid> --timeline-id <guid> --expected-project-id <guid> --output <new-adjacent.jsonl> [--preflight]";
    public static EvaluatorCommand Parse(string[] args)
    {
        if (args is ["--list-scopes"]) return new(null, null, Guid.Empty, Guid.Empty, Guid.Empty, false, true);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var preflight = false;
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (key == "--preflight" && !preflight) { preflight = true; continue; }
            if (key is not ("--input" or "--output" or "--actor-id" or "--timeline-id" or "--expected-project-id")
                || ++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(args[i]) || !values.TryAdd(key, args[i])) throw new ArgumentException(Usage);
        }
        string Required(string key) => values.GetValueOrDefault(key) ?? throw new ArgumentException(Usage);
        Guid Id(string key) => Guid.TryParse(Required(key), out var id) && id != Guid.Empty ? id : throw new ArgumentException(Usage);
        return new(Path.GetFullPath(Required("--input")), Path.GetFullPath(Required("--output")),
            Id("--actor-id"), Id("--timeline-id"), Id("--expected-project-id"), preflight, false);
    }
}
