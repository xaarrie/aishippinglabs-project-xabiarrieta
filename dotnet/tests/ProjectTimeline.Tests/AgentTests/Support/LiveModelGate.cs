using Xunit;

namespace ProjectTimeline.Tests.AgentTests.Support;

// This collection is also used by transport controls; DisableParallelization prevents overlap
// with other collections, including the legacy test that temporarily clears OPENAI_API_KEY.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IsolatedAgentCollection
{
    public const string Name = "Isolated optimizer evaluations";
}

internal static class LiveModelGate
{
    public const string Variable = "PROJECT_TIMELINE_RUN_LIVE_AGENT_TESTS";
    public const string DisabledReason = "Live model disabled: set PROJECT_TIMELINE_RUN_LIVE_AGENT_TESTS=1 and preconfigure OPENAI_API_KEY in the environment or dedicated test User Secrets.";
    public static string? SkipReason(string? gate) => gate == "1" ? null : DisabledReason;

    // Accept a Boolean, never a key value, so deterministic controls need no credentials.
    public static void RequireCredentials(bool configured)
    {
        if (!configured)
            throw new InvalidOperationException("Live optimizer evaluations require a preconfigured OPENAI_API_KEY in the environment or dedicated test User Secrets. No model was invoked.");
    }
}

public sealed class LiveModelFactAttribute : FactAttribute
{
    public LiveModelFactAttribute() => Skip = LiveModelGate.SkipReason(Environment.GetEnvironmentVariable(LiveModelGate.Variable));
}

public sealed class LiveModelTheoryAttribute : TheoryAttribute
{
    public LiveModelTheoryAttribute() => Skip = LiveModelGate.SkipReason(Environment.GetEnvironmentVariable(LiveModelGate.Variable));
}
