using Xunit;
using System.Text.RegularExpressions;

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
        Assert.Contains("function Shell", source);
        Assert.Contains("href=\"/projects\"", source);
        Assert.Contains("href=\"/profile\"", source);
        Assert.Contains("href=\"/users\"", source);
        Assert.Contains("user.roles.includes(\"Administrator\")", source);
        Assert.Contains("aria-controls=\"app-navigation\"", source);
        Assert.Contains("aria-expanded={sidebarOpen}", source);
        Assert.Contains("aria-hidden={!sidebarOpen}", source);
        Assert.Contains("useRef", source);
        Assert.Contains("menuButtonRef", source);
        Assert.Contains("navigationRef", source);
        Assert.Contains("event.key === \"Escape\"", source);
        Assert.Contains("requestAnimationFrame", source);
        Assert.Contains("onClick={closeSidebar}", source);
        Assert.Contains("aria-current={active(\"/projects\") ? \"page\" : undefined}", source);
        Assert.Contains("className=\"overlay-dismiss\"", source);
        Assert.Contains("role=\"toolbar\"", source);
        Assert.Contains("className=\"status-bar\"", source);
        Assert.Contains("--surface-app", File.ReadAllText(Path.Combine(root, "dotnet", "src", "ProjectTimeline.UI.WebApi", "ClientApp", "src", "styles.css")));
        var styles = File.ReadAllText(Path.Combine(root, "dotnet", "src", "ProjectTimeline.UI.WebApi", "ClientApp", "src", "styles.css"));
        Assert.Contains("var(--color-primary,", styles);
        Assert.Contains("--ui-space-", styles);
        Assert.Contains("--ui-size-breakpoint", styles);
        Assert.Contains("var(--ui-", styles);
        Assert.DoesNotContain("padding: 16px", styles);
        Assert.DoesNotContain("fonts.googleapis.com", source);
        Assert.DoesNotContain("cdnjs.cloudflare.com", source);
        Assert.DoesNotContain("use.fontawesome.com", source);
        Assert.DoesNotContain("setup-fonts", source);
        Assert.Contains("--ui-size-breakpoint: 700px", styles);
        Assert.Contains("--ui-transparent", styles);
        Assert.Contains("--ui-grid-card", styles);
        Assert.Contains("--ui-negative-one", styles);
        Assert.DoesNotContain("minmax(", styles[styles.IndexOf("* {", StringComparison.Ordinal)..]);
        var componentStyles = styles[styles.IndexOf("* {", StringComparison.Ordinal)..];
        Assert.DoesNotMatch(
            new Regex(@"\b(?:margin|padding|gap|width|height|min-width|min-height|max-width|max-height|top|right|bottom|left|inset|grid-template-columns|grid-template-rows|border(?:-radius)?|outline-offset|font-size|letter-spacing)\s*:\s*(?:#[0-9a-fA-F]{3,8}|rgba?\(|transparent\b|-?\d+(?:\.\d+)?(?:px|vh|vw|%|fr|em|rem)?\b)", RegexOptions.CultureInvariant),
            componentStyles);
        Assert.Contains("path === \"/profile\"", source);
        Assert.Contains("path === \"/users\"", source);
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
