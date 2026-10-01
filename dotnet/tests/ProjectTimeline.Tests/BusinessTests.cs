using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.DataAccessLogic;
using Xunit;
using System.Security.Claims;
using System.Text.Json;

namespace ProjectTimeline.Tests;
public sealed class BusinessTests
{
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
