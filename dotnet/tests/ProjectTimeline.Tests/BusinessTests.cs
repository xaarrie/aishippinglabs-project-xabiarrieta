using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.DataAccessLogic;
using ProjectTimeline.UI.WebApi;
using ProjectTimeline.BusinessLogic.Agents;
using Xunit;
using System.Security.Claims;
using System.Text.Json;
using System.Text;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.BusinessLogic.Agents.Tools;

namespace ProjectTimeline.Tests;
public sealed class BusinessTests
{
    [Fact]
    public void OpenAI_client_requires_environment_api_key()
    {
        var original = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
            var exception = Assert.Throws<InvalidOperationException>(() => new CustomOpenAIClient());
            Assert.Contains("OPENAI_API_KEY", exception.Message);
            Assert.DoesNotContain("sk-", exception.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", original);
        }
    }

    [Fact]
    public void Entry_list_preview_is_unicode_bounded_and_detail_keeps_full_content()
    {
        var author = new User { Email = "author@example.test", DisplayName = "Safe Author", PasswordHash = "hash" };
        var entry = new TimelineEntry { TimelineId = Guid.NewGuid(), Author = author, Title = "Title", Content = string.Concat(Enumerable.Repeat("🙂", 501)), Status = "Draft" };
        var list = ApiMappers.ToListDto(entry);
        var detail = ApiMappers.ToDetailDto(entry);
        Assert.Equal(500, list.Preview.EnumerateRunes().Count());
        Assert.Equal(499, list.Preview[..^1].EnumerateRunes().Count());
        Assert.EndsWith("…", list.Preview);
        Assert.Equal(entry.Content, detail.Content);
        Assert.Equal("Safe Author", list.Author.DisplayName);
        Assert.Empty(list.Artifacts);
    }

    [Fact]
    public async Task Artifact_copy_failure_cleans_partial_target()
    {
        var root = Path.Combine(Path.GetTempPath(), "timeline-artifacts-tests", Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ArtifactStorage:BasePath"] = root }).Build();
        var storage = new ArtifactStorage(configuration);
        await Assert.ThrowsAsync<IOException>(() => storage.SaveAsync("P1", "T1", 1, "broken.pdf", "application/pdf", 12, new ThrowingStream(Encoding.ASCII.GetBytes("%PDF-1.7xxxx")), default));
        Assert.False(Directory.Exists(root) && Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any());
    }

    private sealed class ThrowingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) => throw new IOException("copy failed");
    }

    [Fact]
    public async Task Artifact_policy_accepts_pdf_rejects_invalid_empty_oversized_and_uses_unique_storage_names()
    {
        var root = Path.Combine(Path.GetTempPath(), "timeline-artifacts-tests", Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ArtifactStorage:BasePath"] = root, ["ArtifactStorage:MaxBytes"] = "16" }).Build();
        var storage = new ArtifactStorage(configuration);
        async Task<string> Save(string name, string type, byte[] bytes) => await storage.SaveAsync("P1", "T1", 1, name, type, bytes.Length, new MemoryStream(bytes), default);
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.7 test");
        var first = await Save("report.pdf", "application/pdf", pdf);
        var second = await Save("report.pdf", "application/pdf", pdf);
        Assert.NotEqual(first, second);
        await Assert.ThrowsAsync<ArgumentException>(() => Save("empty.pdf", "application/pdf", []));
        await Assert.ThrowsAsync<ArgumentException>(() => Save("bad.exe", "application/octet-stream", [1, 2]));
        await Assert.ThrowsAsync<ArgumentException>(() => Save("bad.pdf", "application/pdf", Encoding.ASCII.GetBytes("not pdf")));
        await Assert.ThrowsAsync<ArgumentException>(() => Save("large.pdf", "application/pdf", Encoding.ASCII.GetBytes("%PDF-123456789012345")));
        await Assert.ThrowsAsync<ArgumentException>(() => Save("..\\secret.pdf", "application/pdf", pdf));
    }
    [Fact]
    public void Artifact_storage_rejects_traversal_and_normalizes_safe_names()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ArtifactStorage:BasePath"] = Path.Combine(Path.GetTempPath(), "timeline-tests") }).Build();
        var storage = new ArtifactStorage(configuration);
        Assert.Throws<ArgumentException>(() => storage.GetSafePath("P1", "T1", 1, "..\\secret.txt"));
        Assert.Throws<ArgumentException>(() => storage.GetSafePath("P1", "T1", 1, "nested/secret.txt"));
        Assert.EndsWith(Path.Combine("P1", "T1", "1", "safe.txt"), storage.GetSafePath("P1", "T1", 1, "safe.txt"));
    }

    [Fact]
    public async Task Entry_numbers_are_sequential_and_audited()
    {
        var options = new DbContextOptionsBuilder<ProjectTimelineDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ProjectTimelineDbContext(options); var user = new User { Email = "a@example.test", DisplayName = "A", PasswordHash = PasswordService.Hash("secret") }; db.Users.Add(user); var project = new Project { Code = "P1", Name = "Project", OwnerId = user.Id }; db.Projects.Add(project); var timeline = new Timeline { Code = "T1", Name = "Timeline", Project = project }; db.Timelines.Add(timeline); await db.SaveChangesAsync();
        var service = new TimelineService(db); var one = await service.AddEntryAsync(timeline.Id, user.Id, "One", "Content", default); var two = await service.AddEntryAsync(timeline.Id, user.Id, "Two", "Content", default);
        Assert.Equal(1, one.EntryNumber); Assert.Equal(2, two.EntryNumber); Assert.NotEqual(default, one.CreatedAtUtc); Assert.True(PasswordService.Verify("secret", user.PasswordHash)); Assert.False(PasswordService.Verify("bad", user.PasswordHash));
    }

    [Fact]
    public async Task Snapshot_numbers_content_and_scoped_search_are_deterministic()
    {
        var options = new DbContextOptionsBuilder<ProjectTimelineDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ProjectTimelineDbContext(options);
        var user = new User { Email = "snapshot@example.test", DisplayName = "Snapshot Author", PasswordHash = "hash" };
        var project = new Project { Code = "PS", Name = "Snapshots", OwnerId = user.Id };
        var timeline = new Timeline { ProjectId = project.Id, Project = project, Code = "TS", Name = "Timeline" };
        db.Users.Add(user); db.Projects.Add(project); db.Timelines.Add(timeline); await db.SaveChangesAsync();
        var service = new TimelineService(db);
        var first = await service.AddEntryAsync(timeline.Id, user.Id, "First", "needle one", default);
        var second = await service.AddEntryAsync(timeline.Id, user.Id, "Second", "needle two", default);
        var otherTimeline = new Timeline { ProjectId = project.Id, Project = project, Code = "OTHER", Name = "Other timeline" };
        db.Timelines.Add(otherTimeline);
        await db.SaveChangesAsync();
        await service.AddEntryAsync(otherTimeline.Id, user.Id, "Other", "needle outside", default);
        var snapshot = await service.CreateSnapshotAsync(timeline.Id, null, [second.Id, first.Id], default);
        Assert.Equal(1, snapshot.SnapshotNumber);
        Assert.Contains("Entry 1: First", snapshot.SummaryContent);
        Assert.Equal([first.Id, second.Id], snapshot.Entries.OrderBy(x => x.Entry.EntryNumber).Select(x => x.EntryId).ToArray());
        var search = new SearchEntries(db, timeline.Id);
        Assert.Equal(new[] { "needle one", "needle two" }, await search.search_entries("needle"));
        Assert.Empty(await search.search_entries("no matching content"));
        Assert.Empty(await search.search_entries("outside"));
    }

    [Fact]
    public async Task Entry_deletion_returns_domain_conflict_when_entry_belongs_to_snapshot()
    {
        var options = new DbContextOptionsBuilder<ProjectTimelineDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ProjectTimelineDbContext(options);
        var user = new User { Email = "delete-snapshot@example.test", DisplayName = "Snapshot Author", PasswordHash = "hash" };
        var project = new Project { Code = "PD", Name = "Deletion", OwnerId = user.Id };
        var timeline = new Timeline { ProjectId = project.Id, Project = project, Code = "TD", Name = "Timeline" };
        db.Users.Add(user); db.Projects.Add(project); db.Timelines.Add(timeline); await db.SaveChangesAsync();
        var service = new TimelineService(db);
        var entry = await service.AddEntryAsync(timeline.Id, user.Id, "Protected", "Content", default);
        await service.CreateSnapshotAsync(timeline.Id, null, [entry.Id], default);

        var exception = await Assert.ThrowsAsync<TimelineEntrySnapshotConflictException>(() => service.DeleteEntryAsync(entry.Id, default));

        Assert.Equal("Timeline entries that belong to a snapshot cannot be deleted.", exception.Message);
        Assert.True(await db.TimelineEntries.AnyAsync(x => x.Id == entry.Id));
    }

    [Fact]
    public async Task Recovery_token_expires_is_one_time_and_resets_with_a_hash()
    {
        var outbox = Path.Combine(Path.GetTempPath(), "timeline-recovery-tests", Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Recovery:OutboxPath"] = outbox, ["Recovery:BaseUrl"] = "http://test" }).Build();
        var options = new DbContextOptionsBuilder<ProjectTimelineDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ProjectTimelineDbContext(options); var user = new User { Email = "recover@example.test", DisplayName = "Recover", PasswordHash = PasswordService.Hash("old-password") }; db.Users.Add(user); await db.SaveChangesAsync();
        var service = new RecoveryService(db, new FileRecoveryOutbox(configuration), configuration);
        await service.RequestAsync(user.Email, default);
        var payload = JsonDocument.Parse(await File.ReadAllTextAsync(Directory.GetFiles(outbox).Single())).RootElement;
        var token = new Uri(payload.GetProperty("recoveryLink").GetString()!).Query.Split('=', 2)[1];
        Assert.True(await service.ResetAsync(Uri.UnescapeDataString(token), "new-password-123", default));
        Assert.False(await service.ResetAsync(Uri.UnescapeDataString(token), "another-password", default));
        Assert.True(PasswordService.Verify("new-password-123", user.PasswordHash));
        var state = await db.PasswordRecoveryStates.SingleAsync(); state.IsUsed = false; state.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1); await db.SaveChangesAsync();
        Assert.False(await service.ResetAsync(Uri.UnescapeDataString(token), "expired-password", default));
    }

    [Fact]
    public async Task Administrator_can_access_unrelated_project_descendants()
    {
        var options = new DbContextOptionsBuilder<ProjectTimelineDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ProjectTimelineDbContext(options); var admin = new User { Email = "admin@example.test", DisplayName = "Admin", PasswordHash = "hash" }; var owner = new User { Email = "owner@example.test", DisplayName = "Owner", PasswordHash = "hash" }; db.Users.AddRange(admin, owner); var project = new Project { Code = "P1", Name = "Other", OwnerId = owner.Id }; db.Projects.Add(project); var timeline = new Timeline { ProjectId = project.Id, Code = "T1", Name = "Timeline" }; db.Timelines.Add(timeline); await db.SaveChangesAsync();
        var authorization = new ProjectAuthorization(db);
        Assert.True(await authorization.CanReadAsync(project.Id, admin.Id, administrator: true));
        Assert.True(await authorization.CanWriteAsync(project.Id, admin.Id, administrator: true));
        Assert.True(await authorization.CanManageAsync(project.Id, admin.Id, administrator: true));
    }

    [Fact]
    public async Task Current_database_role_authorizes_admin_even_when_token_claim_is_stale()
    {
        var options = new DbContextOptionsBuilder<ProjectTimelineDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ProjectTimelineDbContext(options);
        var role = new UserRole { RoleName = "Admin" };
        var user = new User { Email = "admin@example.test", DisplayName = "Admin", PasswordHash = "hash", IDUserRole = role.Id, UserRole = role };
        db.UserRoles.Add(role); db.Users.Add(user); await db.SaveChangesAsync();
        var claims = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, "Administrator")]));
        var context = new Microsoft.AspNetCore.Authorization.AuthorizationHandlerContext([new AdminRequirement()], claims, null);
        await new CurrentUserAdminHandler(db).HandleAsync(context);
        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public void Current_project_owner_cannot_be_removed_but_other_collaborators_can()
    {
        var owner = new User { DisplayName = "Owner" };
        var project = new Project { OwnerId = owner.Id, Owner = owner };
        Assert.False(ProjectService.CanRemoveParticipant(project, new ProjectParticipant { UserId = owner.Id }));
        Assert.True(ProjectService.CanRemoveParticipant(project, new ProjectParticipant { UserId = Guid.NewGuid() }));
    }
}
