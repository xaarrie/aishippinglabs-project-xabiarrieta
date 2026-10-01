using Microsoft.EntityFrameworkCore;
using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.Database;
public static class DatabaseSetup
{
    public static async Task MigrateAndSeedAsync(ProjectTimelineDbContext db, string passwordHash, CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);
        // The repository carries a provider-neutral development migration marker; EnsureCreated keeps a fresh local database usable when the EF CLI is not installed.
        await db.Database.EnsureCreatedAsync(ct);
        var adminUser = await db.Users.SingleOrDefaultAsync(x => x.Email == "x.arrieta@ibernova.com", ct); if (adminUser is null) { adminUser = new User { Email = "x.arrieta@ibernova.com", DisplayName = "Development Administrator", PasswordHash = passwordHash }; db.Users.Add(adminUser); await db.SaveChangesAsync(ct); }
        foreach (var role in new[] { "Administrator", "Consultant", "UserChief" }) if (!await db.UserRoles.AnyAsync(x => x.UserId == adminUser.Id && x.RoleName == role, ct)) db.UserRoles.Add(new UserRole { UserId = adminUser.Id, RoleName = role });
        await db.SaveChangesAsync(ct);
    }
}
