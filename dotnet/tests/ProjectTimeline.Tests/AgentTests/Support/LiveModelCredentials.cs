using Microsoft.Extensions.Configuration;

namespace ProjectTimeline.Tests.AgentTests.Support;

internal static class LiveModelCredentials
{
    private const string Key = "OPENAI_API_KEY";

    // No default sources are selected until the paid-live gate is explicitly enabled.
    public static string ResolveForLiveExecution(string? gate) => ResolveForLiveExecution(gate,
        () => Resolve(() => Environment.GetEnvironmentVariable(Key), ReadUserSecrets));

    public static string ResolveForLiveExecution(string? gate, Func<string> resolve)
    {
        if (LiveModelGate.SkipReason(gate) is { } disabledReason)
            throw new InvalidOperationException(disabledReason);
        return resolve();
    }

    // Both sources are injected in deterministic tests; no developer store is opened there.
    public static string Resolve(Func<string?> readEnvironment, Func<string?> readUserSecrets)
    {
        string? key;
        try
        {
            key = readEnvironment();
            if (string.IsNullOrWhiteSpace(key)) key = readUserSecrets();
        }
        catch
        {
            // Configuration exceptions can include file paths, malformed content or credentials.
            throw new InvalidOperationException("Live optimizer credentials could not be read. Configure OPENAI_API_KEY in the environment or dedicated test User Secrets. No model was invoked.");
        }
        LiveModelGate.RequireCredentials(!string.IsNullOrWhiteSpace(key));
        return key!;
    }

    private static string? ReadUserSecrets()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets(typeof(LiveModelCredentials).Assembly, optional: true, reloadOnChange: false)
            .Build();
        try { return configuration[Key]; }
        finally { (configuration as IDisposable)?.Dispose(); }
    }
}
