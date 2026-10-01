using Xunit;

namespace ProjectTimeline.Tests;

public sealed class HostedUiTests
{
    [Fact]
    public void Hosted_asset_contains_required_workflow_labels()
    {
        var root = FindRepositoryRoot(AppContext.BaseDirectory);
        var script = File.ReadAllText(Path.Combine(root, "dotnet", "src", "ProjectTimeline.UI.WebApi", "wwwroot", "app.js"));
        Assert.Contains("Request password recovery", script);
        Assert.Contains("Save profile", script);
        Assert.Contains("Manage admin users", script);
        Assert.Contains("Manage participants", script);
        Assert.Contains("Timeline workspace", script);
        Assert.Contains("Artifact upload", script);
    }

    private static string FindRepositoryRoot(string start)
    {
        var current = new DirectoryInfo(start);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "dotnet", "ProjectTimeline.sln"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
