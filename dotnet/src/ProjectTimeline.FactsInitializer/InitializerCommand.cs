using System.Globalization;

namespace ProjectTimeline.FactsInitializer;

public static class InitializerCommand
{
    public const string Usage = "Usage: ProjectTimeline.FactsInitializer --actor-id <guid> [--page-size <1-100>] [--apply] [--retry-existing --timeline-id <guid>]\nDefault: dry-run (WouldSave/WouldRetry). --apply persists pending extraction candidates; Saved means nonempty pending evidence, not active facts. Activation requires explicit reconciliation acceptance elsewhere; this command never accepts or queues embeddings. Retry mode never enriches existing facts.";

    public static TimelineFactRetryOptions ParseRetry(string[] args)
    {
        Guid timeline = Guid.Empty;
        var retry = false;
        var remaining = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            if (!seen.Add(option)) throw new ArgumentException("Duplicate option.");
            if (option == "--retry-existing") retry = true;
            else if (option == "--timeline-id")
            {
                if (++i >= args.Length || !Guid.TryParse(args[i], out timeline) || timeline == Guid.Empty)
                    throw new ArgumentException("Invalid timeline.");
            }
            else
            {
                remaining.Add(option);
                if (option is "--actor-id" or "--page-size" && i + 1 < args.Length) remaining.Add(args[++i]);
            }
        }
        if (!retry || timeline == Guid.Empty) throw new ArgumentException("Explicit retry and timeline required.");
        var legacy = Parse(remaining.ToArray());
        return new(legacy.ActorId, timeline, legacy.Apply, legacy.PageSize);
    }

    public static InitializerOptions Parse(string[] args)
    {
        Guid actor = Guid.Empty;
        var apply = false;
        var pageSize = 50;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            if (!seen.Add(option)) throw new ArgumentException("Duplicate option.");
            switch (option)
            {
                case "--apply":
                    apply = true;
                    break;
                case "--actor-id" when i + 1 < args.Length:
                    if (!Guid.TryParse(args[++i], out actor) || actor == Guid.Empty)
                        throw new ArgumentException("Invalid actor.");
                    break;
                case "--page-size" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out pageSize)
                        || pageSize is < 1 or > 100) throw new ArgumentException("Invalid page size.");
                    break;
                default:
                    throw new ArgumentException("Unknown or incomplete option.");
            }
        }
        if (actor == Guid.Empty) throw new ArgumentException("Actor required.");
        return new(actor, apply, pageSize);
    }
}
