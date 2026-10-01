using Microsoft.EntityFrameworkCore;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.DataAccessLogic;
using Xunit;

namespace ProjectTimeline.Tests;
public sealed class BusinessTests
{
    [Fact]
    public async Task Entry_numbers_are_sequential_and_audited()
    {
        var options = new DbContextOptionsBuilder<ProjectTimelineDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ProjectTimelineDbContext(options); var user = new User { Email = "a@example.test", DisplayName = "A", PasswordHash = PasswordService.Hash("secret") }; db.Users.Add(user); var project = new Project { Code = "P1", Name = "Project", OwnerId = user.Id }; db.Projects.Add(project); var timeline = new Timeline { Code = "T1", Name = "Timeline", Project = project }; db.Timelines.Add(timeline); await db.SaveChangesAsync();
        var service = new TimelineService(db); var one = await service.AddEntryAsync(timeline.Id, user.Id, "One", "Content", default); var two = await service.AddEntryAsync(timeline.Id, user.Id, "Two", "Content", default);
        Assert.Equal(1, one.EntryNumber); Assert.Equal(2, two.EntryNumber); Assert.NotEqual(default, one.CreatedAtUtc); Assert.True(PasswordService.Verify("secret", user.PasswordHash)); Assert.False(PasswordService.Verify("bad", user.PasswordHash));
    }
}
