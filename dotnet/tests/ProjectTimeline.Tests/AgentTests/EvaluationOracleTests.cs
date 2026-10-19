using ProjectTimeline.Tests.AgentTests.Support;
using Xunit;

namespace ProjectTimeline.Tests.AgentTests;

// Retained live-execution safeguards; rewrite-specific semantic oracles are retired.
public sealed class EvaluationOracleTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("1", true)]
    public void Live_gate_requires_exact_opt_in_and_explicit_missing_credentials_fail_before_any_model(string? value, bool enabled)
    {
        Assert.Equal(enabled, LiveModelGate.SkipReason(value) is null);
        var error = Assert.Throws<InvalidOperationException>(() => LiveModelGate.RequireCredentials(false));
        Assert.Contains("preconfigured OPENAI_API_KEY", error.Message);
        Assert.DoesNotContain("review context could not be read", error.Message);
        LiveModelGate.RequireCredentials(true);
    }
}
