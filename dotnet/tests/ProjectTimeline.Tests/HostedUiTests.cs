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
        Assert.Contains("go(`/projects/${projectId}/timelines/${timelineId}/entries/${id}`, { state: { entrySave:", source);
        Assert.DoesNotContain("go(`/projects/${projectId}/timelines/${timelineId}`)", source);
        Assert.Contains("const entryNew = path.match", source);
        Assert.Contains("const entryDetail = path.match", source);
        Assert.Contains("accept=\".pdf,.doc,.docx,.xls,.xlsx,.md,.markdown\"", source);
        Assert.Contains("A failed upload keeps the selected file", source);
        Assert.Contains("if (File.Exists(path)) File.Delete(path)", File.ReadAllText(Path.Combine(root, "dotnet", "src", "ProjectTimeline.BusinessLogic", "ArtifactStorage.cs")));
        Assert.DoesNotContain("defaultValue={entry.content}", source);
        Assert.Contains("/api/projects/${project.id}/participants", source);
        Assert.Contains("Artifact upload", source);
        Assert.Contains("function Shell", source);
        Assert.Contains("export function resolveRoute", source);
        Assert.Contains("function useLocationState", source);
        Assert.Single(Regex.Matches(source, "addEventListener\\(\\\"popstate\\\""));
        Assert.Contains("project-timeline:navigation", source);
        Assert.Contains("addEventListener(SPA_NAVIGATION_EVENT", source);
        Assert.Contains("removeEventListener(\"popstate\", update)", source);
        Assert.Contains("removeEventListener(SPA_NAVIGATION_EVENT, update)", source);
        Assert.Contains("new Event(SPA_NAVIGATION_EVENT)", source);
        Assert.DoesNotContain("function Shell({ user, path", source);
        Assert.DoesNotContain("const [path, setPath]", source);
        Assert.Contains("preventDefault()", source);
        Assert.Contains("history.pushState", source);
        Assert.DoesNotContain("new PopStateEvent(\"popstate\")", source);
        Assert.Contains("replaceState", source);
        Assert.Contains("path === \"/\") return { path: \"/projects\", kind: \"projects\"", source);
        Assert.Contains("Restoring session...", source);
        Assert.Contains("location.pathname === \"/\"", source);
        Assert.Contains("new AbortController()", source);
        Assert.Contains("controller.abort()", source);
        Assert.Contains("signal: controller.signal", source);
        Assert.DoesNotMatch(new Regex(@"useEffect\s*\(\s*load\s*,", RegexOptions.CultureInvariant), source);
        Assert.Contains("function useResource(path, initial)", source);
        Assert.Contains("class RouteErrorBoundary", source);
        Assert.Contains("<RouteErrorBoundary>{content}</RouteErrorBoundary>", source);
        Assert.Contains("Unable to load this project.", source);
        Assert.Contains("function navigate(path,", source);
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
        Assert.Contains("timeline.data.projectId !== project.data.id", source);
        Assert.Contains("value.projectId === projectId", source);
         Assert.Contains("function DataTable", source);
         Assert.Contains("sorted.length ?", source);
         Assert.Contains("className=\"empty-state\"", source);
         Assert.Contains("aria-sort", source);
         Assert.Contains("Search by email, name, or role", source);
         Assert.Contains("/api/admin/roles", source);
         Assert.Contains("function RoleSelect", source);
         Assert.Contains("name=\"IDUserRole\"", source);
         Assert.DoesNotContain("Roles (comma-separated)", source);
         Assert.Contains("Project owner", source);
         Assert.Contains("Only Administrators can reassign ownership", source);
         Assert.Contains("ownerDisplayName", source);
         Assert.Contains("AuthorDisplayName", File.ReadAllText(Path.Combine(root, "dotnet", "src", "ProjectTimeline.BusinessLogic", "Contracts.cs")));
         Assert.Contains("Rows per page", source);
         Assert.Contains("function StatusBadge", source);
         Assert.Contains("function RoleBadge", source);
         Assert.Contains("Immutable project identifier", source);
         Assert.Contains("Account identity", source);
         Assert.Contains("Project description", source);
         Assert.Contains("title={x.description || \"No description\"}", source);
         Assert.Contains("<table>", source);
        Assert.Contains("window.confirm", source);
        Assert.Contains("soft-deactivated", source);
        Assert.Contains("onSaved({ ...user, ...updated })", source);
        Assert.DoesNotContain("location.reload", source);
        Assert.DoesNotContain("<a href=\"/", source);
         Assert.Contains("(user.roleCode || user.roleName) === \"Admin\"", source);
        Assert.Contains("aria-controls=\"app-navigation\"", source);
        Assert.Contains("aria-expanded={sidebarOpen}", source);
        Assert.Contains("aria-hidden={!sidebarOpen}", source);
        Assert.Contains("useRef", source);
        Assert.Contains("menuButtonRef", source);
        Assert.Contains("navigationRef", source);
        Assert.Contains("event.key === \"Escape\"", source);
        Assert.Contains("target.focus({ preventScroll: true })", source);
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
         Assert.Contains("-webkit-line-clamp: var(--ui-line-clamp)", styles);
         Assert.Contains("--ui-line-clamp: 2", styles);
         Assert.Contains("flex-wrap: wrap", styles);
         Assert.DoesNotContain("flex-wrap: nowrap", styles);
        Assert.Contains("--ui-negative-one", styles);
        Assert.Contains("--ui-track-fluid: minmax(0, 1fr)", styles);
        Assert.Contains("grid-template-rows: var(--ui-size-topbar) var(--ui-track-fluid) var(--ui-size-status)", styles);
        Assert.DoesNotContain("@media (max-width: var(", styles);
        Assert.Contains("@media (max-width: 700px)", styles);
        Assert.DoesNotContain("grid-template-columns: var(--ui-flex-one)", styles);
        var componentStyles = styles[styles.IndexOf("* {", StringComparison.Ordinal)..];
        // Media conditions require literals; declarations inside their rules still require tokens.
        componentStyles = Regex.Replace(componentStyles, @"@media[^{]*\{", "{", RegexOptions.CultureInvariant);
        Assert.DoesNotMatch(
            new Regex(@"\b(?:margin|padding|gap|width|height|min-width|min-height|max-width|max-height|top|right|bottom|left|inset|grid-template-columns|grid-template-rows|border(?:-radius)?|outline-offset|font-size|letter-spacing|line-height|font-weight|color|background(?:-color)?|border-color|outline|opacity|z-index|flex(?:-shrink)?|-webkit-line-clamp)\s*:\s*(?:#[0-9a-fA-F]{3,8}|rgba?\(|transparent\b|-?\d+(?:\.\d+)?(?:px|vh|vw|%|fr|em|rem)?\b)", RegexOptions.CultureInvariant),
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

    [Fact]
    public void Shell_navigation_guards_hidden_and_modal_focus_lifecycle()
    {
        var root = FindRepositoryRoot(AppContext.BaseDirectory);
        var source = File.ReadAllText(Path.Combine(root, "dotnet", "src", "ProjectTimeline.UI.WebApi", "ClientApp", "src", "App.jsx"));
        Assert.Contains("hidden={!sidebarOpen}", source);
        Assert.Contains("role={drawerOpen ? \"dialog\" : undefined}", source);
        Assert.Contains("aria-modal={drawerOpen ? true : undefined}", source);
        Assert.Contains("query.addEventListener(\"change\", update)", source);
        Assert.Contains("query.removeEventListener(\"change\", update)", source);
        Assert.Contains("element.inert = true", source);
        Assert.Contains("element.inert = inert", source);
        Assert.Contains("element.style.setProperty(property, value, priority)", source);
        Assert.Contains("event.key === \"Tab\"", source);
        Assert.Contains("document.removeEventListener(\"focusin\", onFocusIn)", source);
        Assert.Contains("document.removeEventListener(\"keydown\", onKeyDown, true)", source);
    }

    [Fact]
    public void Shared_primitives_keep_field_feedback_and_semantic_control_contracts()
    {
        var root = FindRepositoryRoot(AppContext.BaseDirectory);
        var client = Path.Combine(root, "dotnet", "src", "ProjectTimeline.UI.WebApi", "ClientApp", "src");
        var source = File.ReadAllText(Path.Combine(client, "App.jsx"));
        var styles = File.ReadAllText(Path.Combine(client, "styles.css"));
        var fields = source[source.IndexOf("function FieldFeedback", StringComparison.Ordinal)..source.IndexOf("function ErrorMessage", StringComparison.Ordinal)];
        Assert.Contains("className=\"field\"", fields);
        Assert.Contains("{hint &&", fields);
        Assert.Contains("{error &&", fields);
        Assert.Contains("htmlFor={id}", fields);
        Assert.Contains("`${id}-hint`", fields);
        Assert.Contains("`${id}-error`", fields);
        Assert.Contains("aria-labelledby={props[\"aria-labelledby\"] || `${id}-label`}", fields);
        Assert.Contains("aria-describedby={fieldDescription", fields);
        Assert.Contains("aria-invalid={error ? true", fields);
        Assert.Contains("<input {...props}", fields);
        Assert.Contains("<select {...props}", fields);
        Assert.DoesNotContain("return <><SelectField", fields);
        Assert.Contains("headingLevel = 1", source);
        Assert.Contains("<Heading id={titleId}>{title}</Heading>", source);
        Assert.Contains("if (disabled) { event.preventDefault(); return; }", source);
        Assert.Contains("button.danger, a.danger", styles);
        Assert.Contains("button:disabled", styles);
        Assert.Contains("a[aria-disabled=\"true\"]", styles);
        Assert.Contains("--ui-size-control: 44px", styles);
        Assert.Contains("--ui-size-choice: 20px", styles);
        Assert.Contains("--ui-line-height-body", styles);
        Assert.Contains("field-sizing: content", styles);
        Assert.Contains("resize: vertical", styles);
        Assert.Contains(".form-grid > * { min-width: var(--ui-zero)", styles);
    }

    [Fact]
    public void Register_and_account_states_preserve_explicit_loading_retry_and_submission_boundaries()
    {
        var root = FindRepositoryRoot(AppContext.BaseDirectory);
        var source = File.ReadAllText(Path.Combine(root, "dotnet", "src", "ProjectTimeline.UI.WebApi", "ClientApp", "src", "App.jsx"));
        var table = source[source.IndexOf("function DataTable", StringComparison.Ordinal)..source.IndexOf("function LoadFeedback", StringComparison.Ordinal)];
        Assert.Contains("loading = false", table);
        Assert.Contains("aria-busy={loading}", table);
        Assert.Contains("!loading && !error", table);
        Assert.Contains("No matching records", table);
        Assert.Contains("Clear filters", table);
        Assert.Contains("emptyAction", table);
        Assert.Contains("hasData={rows.length > 0}", table);
        var managed = source[source.IndexOf("function ManagedForm", StringComparison.Ordinal)..source.IndexOf("function Login", StringComparison.Ordinal)];
        Assert.Contains("if (locked.current) return", managed);
        Assert.Contains("locked.current = true", managed);
        Assert.Contains("finally { locked.current = false; }", managed);
        Assert.Contains("disabled={state.pending}", managed);
        Assert.Contains("aria-busy={state.pending}", managed);
        Assert.Contains("your entered values are retained", managed);
        Assert.Contains("Profile saved.", source);
        Assert.Contains("kind === \"user-edit\" ? { id: match[1] }", source);
        Assert.Contains("Available records are still shown below.", source);
        Assert.Contains("Read full description", source);
        Assert.Contains("className=\"danger\" disabled={action.pending}", source);
    }

    [Fact]
    public void Register_actions_use_native_disclosure_and_restore_focus_without_full_width_growth()
    {
        var root = FindRepositoryRoot(AppContext.BaseDirectory);
        var client = Path.Combine(root, "dotnet", "src", "ProjectTimeline.UI.WebApi", "ClientApp", "src");
        var source = File.ReadAllText(Path.Combine(client, "App.jsx"));
        var styles = File.ReadAllText(Path.Combine(client, "styles.css"));
        var actions = source[source.IndexOf("function RecordActions", StringComparison.Ordinal)..source.IndexOf("function StatusBadge", StringComparison.Ordinal)];
        Assert.Contains("<details className=\"record-more\"", actions);
        Assert.Contains("<summary aria-label={`More actions for ${label}`}>", actions);
        Assert.Contains("event.key === \"Escape\"", actions);
        Assert.Contains("event.currentTarget.open = false", actions);
        Assert.Contains("querySelector(\"summary\").focus()", actions);
        Assert.Contains("ariaLabel={`Delete project ${x.code}`}", source);
        Assert.Contains("ariaLabel={`Delete user ${x.email}`}", source);
        Assert.Contains("flex: var(--ui-flex-fixed)", styles);
        Assert.DoesNotContain(".record-actions > .primary { flex-basis: var(--ui-full)", styles);
        Assert.Contains(".record-secondary-actions .danger:enabled", styles);
        Assert.Contains("Read full description for project", source);
    }

    [Fact]
    public void Workspace_composition_keeps_safe_content_explicit_requests_and_review_source_boundaries()
    {
        var root = FindRepositoryRoot(AppContext.BaseDirectory);
        var client = Path.Combine(root, "dotnet", "src", "ProjectTimeline.UI.WebApi", "ClientApp", "src");
        var source = File.ReadAllText(Path.Combine(client, "App.jsx"));
        var styles = File.ReadAllText(Path.Combine(client, "styles.css"));
        var workspace = source[source.IndexOf("function TimelineEdit", StringComparison.Ordinal)..source.IndexOf("function NotFound", StringComparison.Ordinal)];
        Assert.Contains("headingLevel={2} title=\"Project timelines\"", workspace);
        Assert.Contains("headingLevel={3}", workspace);
        Assert.Contains("headingLevel={4} title=\"Timeline entries\"", workspace);
        Assert.Contains("white-space: pre-wrap", styles);
        Assert.DoesNotContain("dangerouslySetInnerHTML", workspace);
        Assert.DoesNotContain("onBlur=", workspace);
        Assert.Contains("ariaLabel=\"Create timeline\"", workspace);
        Assert.Contains("ariaLabel=\"Create timeline entry\"", workspace);
        Assert.Contains("aria-busy={action.pending}", workspace);
        Assert.Contains("if (saveLock.current) return", workspace);
        Assert.Contains("body.append(\"file\", file)", workspace);
        Assert.Contains("entryIds: selected", workspace);
        Assert.Contains("<legend>Select entries</legend>", workspace);
        Assert.Contains("onRetry={reload}", workspace);
        Assert.Contains("hasData={!!entries.length}", workspace);
        Assert.Contains("dirty={reviewDirty} onActiveChange={setReviewActive}", workspace);
        var factReview = File.ReadAllText(Path.Combine(client, "FactReview.jsx"));
        var factClient = File.ReadAllText(Path.Combine(client, "factReview.js"));
        Assert.Contains("import FactReview from \"./FactReview.jsx\";", source);
        Assert.Contains("<FactReview key={id} entry={entry} canWrite={canWrite && !saving} dirty={reviewDirty} onActiveChange={setReviewActive} onStateChange={setFactState} />", workspace);
        var detail = workspace[workspace.IndexOf("function EntryDetail", StringComparison.Ordinal)..workspace.IndexOf("function EntryCreate", StringComparison.Ordinal)];
        var detailReview = Assert.Single(Regex.Matches(detail, @"<FactReview\b.*? />", RegexOptions.Singleline).Cast<Match>()).Value;
        foreach (var prop in new[] { "key={id}", "entry={entry}", "canWrite={canWrite}", "onActiveChange={setReviewActive}", "onStateChange={setFacts}", "refreshKey={factRefreshKey}" })
            Assert.Contains(prop, detailReview);
        Assert.Contains("const [factRefreshKey, setFactRefreshKey] = useState(0)", detail);
        Assert.Contains("import EntryClarifications from \"./EntryClarifications.jsx\";", source);
        var clarifications = Assert.Single(Regex.Matches(detail, @"<EntryClarifications\b.*? />", RegexOptions.Singleline).Cast<Match>()).Value;
        foreach (var prop in new[] { "key={`clarifications-${id}`}", "entryId={id}", "canWrite={canWrite}", "blocked={reviewActive}", "refreshKey={`${facts?.generationId ?? ''}:${facts?.lifecycleState ?? ''}`}", "onResume={() => setFactRefreshKey(value => value + 1)}", "onActiveChange={setClarificationActive}" })
            Assert.Contains(prop, clarifications);
        Assert.DoesNotContain("onSave=", clarifications);
        Assert.DoesNotContain("source=", clarifications);
        Assert.DoesNotContain("content=", clarifications);
        Assert.Contains("from \"./entryAccess.js\"", source);
        Assert.Contains("createEntrySaveAction(factClient)", workspace);
        Assert.Contains("factReviewActions(factState, { canWrite }).edit", workspace);
        Assert.DoesNotContain("<EntryReview", workspace);
        Assert.DoesNotContain("applyReviewDraft", workspace);
        Assert.DoesNotContain("onSave=", workspace);
        Assert.Contains("client.accept(entry.id, proposal.proposalId, { signal })", factReview);
        Assert.Contains("client.read(entry.id, { signal })", factReview);
        Assert.Contains("<h4>{source.title}</h4><pre className=\"entry-content\">{source.content}</pre>", factReview);
        Assert.Contains("savedFactSource(state, entry.id)", factReview);
        Assert.Contains("entryMatchesFactSource(entry, source)", workspace);
        Assert.Contains("Reload saved source (discard draft)", workspace);
        Assert.Contains("Original entry and title are unchanged.", factReview);
        Assert.DoesNotContain("onSave", factReview);
        Assert.DoesNotContain("setEditor", factReview);
        Assert.DoesNotContain("applyReviewDraft", factReview);
        var accept = Assert.Single(factClient.Split('\n'), line => line.TrimStart().StartsWith("accept:", StringComparison.Ordinal));
        Assert.Contains("/fact-proposals/${encodeURIComponent(proposalId)}/accept", accept);
        Assert.Contains("{ ...options, method: 'POST' })", accept);
        Assert.DoesNotContain("body:", accept);
        Assert.DoesNotContain("PUT", factClient);
        Assert.Contains("hasEntryWriteAccess(user, project, participants)", workspace);
        Assert.Contains("controller.signal.aborted", workspace);
        Assert.Contains("record-actions", source);
        Assert.Contains("grid-template-columns: var(--ui-grid-workspace)", styles);
    }

    private static string FindRepositoryRoot(string start)
    {
        var current = new DirectoryInfo(start);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "dotnet", "ProjectTimeline.sln"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
