using Microsoft.Extensions.AI;

namespace ProjectTimeline.BusinessLogic.Agents.Framework;

/// <summary>Opt-in task-based provider limits. These do not bound input tokens or internal transport retries.</summary>
public sealed record AgentExecutionBudgetOptions(int MaxProviderCalls, int MaxOutputTokens, TimeSpan Timeout);

public enum AgentExecutionBudgetReason { ProviderCallLimit, RequestCanceled, DeadlineExceeded, ScopeDisposed }

/// <summary>Safe, stable reason without provider exceptions, prompts, or content.</summary>
public sealed class AgentExecutionBudgetException : OperationCanceledException
{
    public AgentExecutionBudgetReason Reason { get; }
    internal AgentExecutionBudgetException(AgentExecutionBudgetReason reason, CancellationToken token)
        : base($"Agent execution stopped: {reason}.", token) => Reason = reason;
}

/// <summary>
/// One logical operation, flowing through ExecutionContext. Start/dispose in the owning flow in LIFO
/// order. Nested scopes are independent and restore their parent. Disposal cancels in-flight work;
/// stale inherited scopes remain closed (never silently become unbounded). Await work before disposal.
/// Use the local wrappers' cancellable typed/text overloads to propagate this deadline to SDK tools.
/// Raw AIAgent/structured callers must pass Token explicitly to bound their whole tool loop, then
/// ThrowIfStopped before publishing results. The leaf alone bounds provider calls, not SDK tool work.
/// Cancellation cannot guarantee that a provider has stopped billing. Use a retry-disabled transport:
/// an arbitrary injected IChatClient can retry internally, outside this admission boundary.
/// </summary>
public sealed class AgentExecutionBudget : IDisposable
{
    private static readonly AsyncLocal<AgentExecutionBudget?> Ambient = new();
    private readonly AgentExecutionBudget? previous;
    private readonly object gate = new();
    private readonly CancellationToken requestToken;
    private readonly CancellationTokenSource deadline;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationTokenSource linked;
    private bool disposed, cancellationCompleted, sourcesDisposed;
    private int active, calls;

    public static AgentExecutionBudget? Current => Ambient.Value;
    public AgentExecutionBudgetOptions Options { get; }
    public CancellationToken Token { get; }
    public int ProviderCallCount { get { lock (gate) return calls; } }

    private AgentExecutionBudget(AgentExecutionBudgetOptions options, CancellationToken requestToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxProviderCalls <= 0 || options.MaxOutputTokens <= 0 || options.Timeout <= TimeSpan.Zero ||
            options.Timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Execution limits must be positive and the timeout supported.");
        Options = options;
        this.requestToken = requestToken;
        previous = Ambient.Value;
        deadline = new CancellationTokenSource(options.Timeout);
        linked = CancellationTokenSource.CreateLinkedTokenSource(requestToken, deadline.Token, lifetime.Token);
        Token = linked.Token;
    }

    public static AgentExecutionBudget Start(AgentExecutionBudgetOptions options, CancellationToken requestToken = default)
    {
        var scope = new AgentExecutionBudget(options, requestToken);
        Ambient.Value = scope;
        return scope;
    }

    private AgentExecutionBudgetException? Failure(CancellationToken caller)
    {
        // Deterministic precedence when multiple stop conditions have fired.
        if (requestToken.IsCancellationRequested) return new(AgentExecutionBudgetReason.RequestCanceled, Token);
        if (deadline.IsCancellationRequested) return new(AgentExecutionBudgetReason.DeadlineExceeded, Token);
        if (disposed) return new(AgentExecutionBudgetReason.ScopeDisposed, Token);
        if (caller.IsCancellationRequested) return new(AgentExecutionBudgetReason.RequestCanceled, Token);
        return null;
    }

    /// <summary>Use Token for tools and this check before publishing an operation result.</summary>
    public void ThrowIfStopped()
    {
        lock (gate) { if (Failure(default) is { } error) throw error; }
    }

    internal void Reserve(CancellationToken caller)
    {
        lock (gate)
        {
            if (Failure(caller) is { } error) throw error;
            if (calls >= Options.MaxProviderCalls)
                throw new AgentExecutionBudgetException(AgentExecutionBudgetReason.ProviderCallLimit, Token);
            calls++; // Never refunded: failures/cancellation may already have incurred provider usage.
        }
    }

    // Wrap the WHOLE agent run so the linked token reaches tools as well as the provider. The leaf
    // guard also uses this helper for callers using the AIAgent factory directly.
    internal static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> run, CancellationToken caller)
    {
        var scope = Current;
        if (scope is null) return await run(caller).ConfigureAwait(false);
        lock (scope.gate)
        {
            if (scope.Failure(caller) is { } error) throw error;
            scope.active++;
        }
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(caller, scope.Token);
            try
            {
                var result = await run(cancellation.Token).ConfigureAwait(false);
                lock (scope.gate) { if (scope.Failure(caller) is { } error) throw error; }
                return result;
            }
            catch (OperationCanceledException)
            {
                lock (scope.gate) { if (scope.Failure(caller) is { } error) throw error; }
                throw;
            }
        }
        finally
        {
            lock (scope.gate) { scope.active--; scope.DisposeSourcesIfIdle(); }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            if (!ReferenceEquals(Ambient.Value, this))
                throw new InvalidOperationException("Dispose execution budgets in LIFO order in the owning logical flow.");
            disposed = true;
            Ambient.Value = previous;
        }
        // Cancellation callbacks can run arbitrary code. Do not invoke them while holding the gate.
        try { lifetime.Cancel(); }
        finally
        {
            lock (gate) { cancellationCompleted = true; DisposeSourcesIfIdle(); }
        }
    }

    private void DisposeSourcesIfIdle()
    {
        if (!cancellationCompleted || active != 0 || sourcesDisposed) return;
        sourcesDisposed = true;
        linked.Dispose();
        deadline.Dispose();
        lifetime.Dispose();
    }
}

/// <summary>
/// Above usage admission, below native chat/tool aggregation. No budget means transparent forwarding.
/// Bounded operations are task-based only; streaming fails explicitly before provider/accounting.
/// </summary>
internal sealed class ExecutionBudgetChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var scope = AgentExecutionBudget.Current;
        if (scope is null) return base.GetResponseAsync(messages, options, cancellationToken);
        return AgentExecutionBudget.RunAsync(async token =>
        {
            var bounded = options?.Clone() ?? new ChatOptions();
            bounded.MaxOutputTokens = Math.Min(bounded.MaxOutputTokens ?? scope.Options.MaxOutputTokens, scope.Options.MaxOutputTokens);
            scope.Reserve(token); // Crucially BEFORE UsageTrackingChatClient.BeginCall.
            return await base.GetResponseAsync(messages, bounded, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (AgentExecutionBudget.Current is not null)
            throw new NotSupportedException("Execution budgets support task-based agent runs only, not streaming.");
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }
}
