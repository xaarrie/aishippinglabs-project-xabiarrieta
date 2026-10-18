using System.Reflection;
using Microsoft.Extensions.Configuration.UserSecrets;
using ProjectTimeline.Tests.AgentTests.Support;
using Xunit;

namespace ProjectTimeline.Tests.AgentTests;

[Collection(IsolatedAgentCollection.Name)]
public sealed class LiveModelCredentialsTests
{
    [Fact]
    public void Test_assembly_has_its_own_explicit_user_secrets_identity()
    {
        var identity = Assert.Single(typeof(LiveModelCredentialsTests).Assembly
            .GetCustomAttributes(typeof(UserSecretsIdAttribute), inherit: false)
            .Cast<UserSecretsIdAttribute>());
        Assert.Equal("ProjectTimeline.Tests-e1899e37-47c2-4d29-9aca-d7e656777469", identity.UserSecretsId);
    }

    [Theory]
    [InlineData("synthetic-environment-override")]
    [InlineData("  synthetic-environment-override  ")]
    public void Nonblank_environment_wins_without_consulting_the_store(string environment)
    {
        var reads = 0;
        var key = LiveModelCredentials.Resolve(() => { reads++; return environment; },
            () => throw new InvalidOperationException("The fallback must not be consulted."));

        Assert.Equal(environment, key);
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\r\n\t")]
    public void Blank_environment_falls_back_to_the_injected_store(string? environment)
    {
        var reads = new List<string>();
        var key = LiveModelCredentials.Resolve(
            () => { reads.Add("environment"); return environment; },
            () => { reads.Add("store"); return "synthetic-local-credential"; });

        Assert.Equal("synthetic-local-credential", key);
        Assert.Equal(["environment", "store"], reads);
    }

    public static IEnumerable<object?[]> MissingSources()
    {
        string?[] blanks = [null, "", " ", "\r\n\t"];
        return from environment in blanks from store in blanks select new object?[] { environment, store };
    }

    [Theory]
    [MemberData(nameof(MissingSources))]
    public void Missing_or_blank_sources_fail_clearly_before_model_invocation(string? environment, string? store)
    {
        var error = Assert.Throws<InvalidOperationException>(() => LiveModelCredentials.Resolve(() => environment, () => store));

        Assert.Contains("preconfigured OPENAI_API_KEY", error.Message);
        Assert.Contains("User Secrets", error.Message);
        Assert.Contains("No model was invoked", error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(false, "format")]
    [InlineData(false, "access")]
    [InlineData(false, "io")]
    [InlineData(true, "format")]
    [InlineData(true, "access")]
    [InlineData(true, "io")]
    public void Source_read_failures_are_sanitized_without_raw_diagnostics_or_inner_errors(bool storeFails, string failure)
    {
        const string privateDetail = "synthetic-private-diagnostic-with-credential-content";
        Exception cause = failure switch {
            "format" => new FormatException(privateDetail, new Exception(privateDetail)),
            "access" => new UnauthorizedAccessException(privateDetail),
            _ => new IOException(privateDetail)
        };
        var error = Assert.Throws<InvalidOperationException>(() => LiveModelCredentials.Resolve(
            () => storeFails ? null : throw cause, () => throw cause));

        Assert.Contains("credentials could not be read", error.Message);
        Assert.Contains("No model was invoked", error.Message);
        Assert.DoesNotContain(privateDetail, error.ToString());
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData(" 1 ")]
    public void Disabled_gate_does_not_select_any_credential_source(string? gate)
    {
        var calls = 0;
        var error = Assert.Throws<InvalidOperationException>(() => LiveModelCredentials.ResolveForLiveExecution(gate,
            () => { calls++; throw new InvalidOperationException("No source should be selected."); }));

        Assert.Equal(LiveModelGate.DisabledReason, error.Message);
        Assert.Equal(0, calls);
        Assert.NotNull(LiveModelGate.SkipReason(gate));
    }

    [Fact]
    public void Enabled_gate_resolves_only_the_explicitly_injected_sources()
    {
        var calls = 0;
        var key = LiveModelCredentials.ResolveForLiveExecution("1", () => {
            calls++;
            return LiveModelCredentials.Resolve(() => null, () => "synthetic-enabled-credential");
        });

        Assert.Equal("synthetic-enabled-credential", key);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Discovery_attributes_do_not_call_credential_selection_or_configuration()
    {
        foreach (var type in new[] { typeof(LiveModelFactAttribute), typeof(LiveModelTheoryAttribute), typeof(LiveModelGate) })
        foreach (var called in CalledMethods(type))
        {
            Assert.NotEqual(typeof(LiveModelCredentials), called.DeclaringType);
            Assert.False(called.DeclaringType?.Namespace?.StartsWith("Microsoft.Extensions.Configuration", StringComparison.Ordinal) == true);
        }
    }

    [Fact]
    public void Credential_resolution_and_live_setup_never_mutate_process_environment()
    {
        // Inspect call sites instead of reading or changing the developer's actual environment key.
        foreach (var type in new[] { typeof(LiveModelCredentials), typeof(OptimizeEntryLiveModelTests), typeof(LiveModelGate) })
        foreach (var called in CalledMethods(type))
            Assert.False(called.DeclaringType == typeof(Environment) && called.Name == nameof(Environment.SetEnvironmentVariable),
                "Live credential setup must not mutate process-global environment.");
    }

    private static IEnumerable<MethodBase> CalledMethods(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var method in type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags)))
        {
            var il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
            for (var offset = 0; offset + 4 < il.Length; offset++)
            {
                if (il[offset] is not (0x28 or 0x6f or 0x73)) continue; // call, callvirt, newobj
                MethodBase? target;
                try { target = method.Module.ResolveMethod(BitConverter.ToInt32(il, offset + 1)); }
                catch (ArgumentException) { continue; } // Operand bytes can resemble opcodes.
                if (target is not null) yield return target;
            }
        }
        foreach (var nested in type.GetNestedTypes(flags))
        foreach (var target in CalledMethods(nested))
            yield return target;
    }
}
