using Xunit;

namespace ProjectTimeline.Tests;

public sealed class HostedUiTests
{
    [Fact]
    public void Hosted_asset_contains_required_workflow_labels()
    {
        var root = FindRepositoryRoot(AppContext.BaseDirectory);
        var source = File.ReadAllText(Path.Combine(root, "dotnet", "src", "ProjectTimeline.UI.WebApi", "ClientApp", "src", "App.jsx"));
        var html = File.ReadAllText(Path.Combine(root, "dotnet", "src", "ProjectTimeline.UI.WebApi", "wwwroot", "index.html"));
        Assert.Contains("from \"react\"", source);
        Assert.Contains("createRoot", File.ReadAllText(Path.Combine(root, "dotnet", "src", "ProjectTimeline.UI.WebApi", "ClientApp", "src", "main.jsx")));
        var login = source[source.IndexOf("function Login", StringComparison.Ordinal)..source.IndexOf("function RecoveryRequest", StringComparison.Ordinal)];
        Assert.Contains("/recovery-request", login);
        Assert.DoesNotContain("Request password recovery", login);
        Assert.DoesNotContain("Reset password", login);
        Assert.Contains("function RecoveryRequest", source);
        Assert.Contains("function ResetPassword", source);
        Assert.Contains("/api/auth/recovery", source);
        Assert.Contains("/api/auth/recovery/reset", source);
        Assert.Contains("/reset-password", source);
        Assert.Contains("Save profile", source);
        Assert.Contains("/api/admin/users", source);
        Assert.Contains("/api/entries/${entry.id}/artifacts", source);
        Assert.Contains("/api/projects/${project.id}/participants", source);
        Assert.Contains("Artifact upload", source);
        Assert.DoesNotContain("app.js", html);
        Assert.Contains("type=\"module\"", html);
        Assert.Contains("assets/", html);
    }

    private static string FindRepositoryRoot(string start)
    {
        var current = new DirectoryInfo(start);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "dotnet", "ProjectTimeline.sln"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
