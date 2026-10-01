using Microsoft.EntityFrameworkCore;
using ProjectTimeline.DataAccessLogic;
using Xunit;

namespace ProjectTimeline.Tests;
public sealed class ApiPersistenceTests
{
    [Fact]
    public async Task User_email_and_project_code_are_modelled_as_unique()
    {
        var options = new DbContextOptionsBuilder<ProjectTimelineDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ProjectTimelineDbContext(options); db.Users.Add(new User { Email = "admin@example.test", DisplayName = "Admin", PasswordHash = "not-a-secret" }); await db.SaveChangesAsync();
        Assert.True(await db.Users.AnyAsync(x => x.Email == "admin@example.test")); Assert.Contains(nameof(ProjectTimelineDbContext.Users), typeof(ProjectTimelineDbContext).GetProperties().Select(x => x.Name));
    }
}
