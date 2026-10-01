using Microsoft.EntityFrameworkCore;
using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.Database;

public static class DatabaseSetup
{
    public static async Task MigrateAndSeedAsync(ProjectTimelineDbContext db, string? adminPasswordHash, CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);
        if (await db.Users.AnyAsync(ct)) return;
        if (string.IsNullOrWhiteSpace(adminPasswordHash))
            throw new InvalidOperationException("First development initialization requires PROJECT_TIMELINE_ADMIN_PASSWORD; no administrator was created.");

        var admin = new User { Email = "x.arrieta@ibernova.com", DisplayName = "Development Administrator", PasswordHash = adminPasswordHash };
        db.Users.Add(admin);
        await db.SaveChangesAsync(ct);
        foreach (var role in new[] { "Administrator", "Consultant", "UserChief" })
            db.UserRoles.Add(new UserRole { UserId = admin.Id, RoleName = role });
        await db.SaveChangesAsync(ct);
    }
}
