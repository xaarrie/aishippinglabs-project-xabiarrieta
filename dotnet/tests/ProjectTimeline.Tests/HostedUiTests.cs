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
        var assetsRoot = Path.Combine(root, "dotnet", "src", "ProjectTimeline.UI.WebApi", "wwwroot");
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
        Assert.Contains("/api/entries/${entryId}/artifacts", source);
        Assert.Contains("/api/entries/${id}", source);
        Assert.Contains("function EntryDetail", source);
        Assert.Contains("projectId={entryDetail[1]}", source);
        Assert.Contains("timelineId={entryDetail[2]}", source);
        Assert.Contains("projectId=${projectId}&timelineId=${timelineId}", source);
        Assert.Contains("The timeline does not belong to this project.", source);
        Assert.DoesNotContain("/projects/undefined", source);
        Assert.Contains("function EntryCreate", source);
        Assert.Contains("function UploadForm", source);
        Assert.Contains("preview", source);
        Assert.Contains("/projects/${projectId}/timelines/${timeline.id}/entries/new", source);
        Assert.Contains("const entryNew = path.match", source);
        Assert.Contains("const entryDetail = path.match", source);
        Assert.Contains("accept=\".pdf,.doc,.docx,.xls,.xlsx,.md,.markdown\"", source);
        Assert.Contains("Select the file again to retry", source);
        Assert.Contains("if (File.Exists(path)) File.Delete(path)", File.ReadAllText(Path.Combine(root, "dotnet", "src", "ProjectTimeline.BusinessLogic", "ArtifactStorage.cs")));
        Assert.DoesNotContain("defaultValue={entry.content}", source);
        Assert.Contains("/api/projects/${project.id}/participants", source);
        Assert.Contains("Artifact upload", source);
        Assert.Contains("function Shell", source);
        Assert.Contains("export function resolveRoute", source);
        Assert.Contains("function useLocationState", source);
        Assert.Single(Regex.Matches(source, "addEventListener\\(\\\"popstate\\\""));
        Assert.DoesNotContain("function Shell({ user, path", source);
        Assert.DoesNotContain("const [path, setPath]", source);
        Assert.Contains("preventDefault()", source);
        Assert.Contains("history.pushState", source);
        Assert.Contains("new PopStateEvent(\"popstate\")", source);
        Assert.Contains("new AbortController()", source);
        Assert.Contains("controller.abort()", source);
        Assert.Contains("signal: controller.signal", source);
        Assert.Contains("Unable to load this project.", source);
        Assert.Contains("function navigate(path)", source);
        Assert.Contains("function Link", source);
        Assert.Contains("addEventListener(\"popstate\"", source);
        Assert.Contains("/users/new", source);
        Assert.Contains("/users/${x.id}/edit", source);
        Assert.Contains("/projects/new", source);
        Assert.Contains("/projects/${x.id}/edit", source);
        Assert.Contains("href={`/projects/${x.id}/timelines`}", source);
        Assert.Contains("const projectTimelines = path.match", source);
        Assert.Contains("const timelineCreate = path.match", source);
        Assert.Contains("const timelineEdit = path.match", source);
        Assert.Contains("/projects/${projectId}/timelines/new", source);
        Assert.Contains("/projects/${projectId}/timelines/${x.id}/edit", source);
        Assert.DoesNotContain("<Link className={active(\"/timelines\")", source);
        Assert.Contains("The timeline does not belong to this project.", source);
        Assert.Contains("timeline.projectId !== project.id", source);
        Assert.Contains("value.projectId === projectId", source);
        Assert.Contains("function DataTable", source);
        Assert.Contains("<table>", source);
        Assert.Contains("window.confirm", source);
        Assert.Contains("soft-deactivated", source);
        Assert.Contains("onSaved({ ...user, ...updated })", source);
        Assert.DoesNotContain("location.reload", source);
        Assert.DoesNotContain("<a href=\"/", source);
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
        Assert.DoesNotContain("path === \"/timelines\"", source);
        Assert.Contains("Page not found", source);
        Assert.DoesNotContain("app.js", html);
        Assert.Contains("type=\"module\"", html);
        Assert.Contains("assets/", html);
        var assetMatches = Regex.Matches(html, "(?:src|href)=\\\"([^\\\"]+assets/[^\\\"]+)\\\"");
        Assert.NotEmpty(assetMatches);
        foreach (Match assetMatch in assetMatches)
        {
            var relativeAsset = assetMatch.Groups[1].Value.TrimStart('.', '/', '\\').Replace('/', Path.DirectorySeparatorChar);
            Assert.True(File.Exists(Path.Combine(assetsRoot, relativeAsset)), $"Hosted asset is missing: {relativeAsset}");
        }
        var jsAsset = assetMatches.Cast<Match>().Select(x => x.Groups[1].Value).FirstOrDefault(x => x.EndsWith(".js", StringComparison.Ordinal));
        Assert.NotNull(jsAsset);
        var bundlePath = jsAsset!.TrimStart('.', '/', '\\').Replace('/', Path.DirectorySeparatorChar);
        var bundle = File.ReadAllText(Path.Combine(assetsRoot, bundlePath));
        Assert.Contains("popstate", bundle);
        Assert.Contains("recovery-request", bundle);
        Assert.Contains("projects", bundle);
    }

    private static string FindRepositoryRoot(string start)
    {
        var current = new DirectoryInfo(start);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "dotnet", "ProjectTimeline.sln"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
