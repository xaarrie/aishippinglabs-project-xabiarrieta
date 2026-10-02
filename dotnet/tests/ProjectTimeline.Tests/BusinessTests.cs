using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.DataAccessLogic;
using ProjectTimeline.UI.WebApi;
using Xunit;
using System.Security.Claims;
using System.Text.Json;
using System.Text;

namespace ProjectTimeline.Tests;
public sealed class BusinessTests
{
    [Fact]
    public void Entry_list_preview_is_unicode_bounded_and_detail_keeps_full_content()
    {
        var author = new User { Email = "author@example.test", DisplayName = "Safe Author", PasswordHash = "hash" };
        var entry = new TimelineEntry { TimelineId = Guid.NewGuid(), Author = author, Title = "Title", Content = string.Concat(Enumerable.Repeat("🙂", 501)), Status = "Draft" };
        var list = ApiMappers.ToListDto(entry);
        var detail = ApiMappers.ToDetailDto(entry);
        Assert.Equal(501, list.Preview.EnumerateRunes().Count());
        Assert.EndsWith("…", list.Preview);
        Assert.Equal(entry.Content, detail.Content);
        Assert.Equal("Safe Author", list.Author.DisplayName);
        Assert.Empty(list.Artifacts);
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
}
