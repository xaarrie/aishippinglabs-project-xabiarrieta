using Microsoft.EntityFrameworkCore;
using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.Database;

public static class DatabaseSetup
{
    public static async Task MigrateAndSeedAsync(ProjectTimelineDbContext db, string? adminPasswordHash, CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);
        await SeedAsync(db, adminPasswordHash, ct);
    }

    public static async Task SeedAsync(ProjectTimelineDbContext db, string? adminPasswordHash, CancellationToken ct = default)
    {
        foreach (var roleName in new[] { "Admin", "Consultant" })
            if (!await db.UserRoles.AnyAsync(x => x.RoleName == roleName, ct))
                db.UserRoles.Add(new UserRole { RoleName = roleName });
        await db.SaveChangesAsync(ct);
        if (await db.Users.AnyAsync(ct)) return;
        if (string.IsNullOrWhiteSpace(adminPasswordHash))
            throw new InvalidOperationException("First development initialization requires PROJECT_TIMELINE_ADMIN_PASSWORD; no administrator was created.");

        var adminRole = await db.UserRoles.SingleAsync(x => x.RoleName == "Admin", ct);
        db.Users.Add(new User { Email = "x.arrieta@ibernova.com", DisplayName = "Development Administrator", PasswordHash = adminPasswordHash, IDUserRole = adminRole.Id });
        await db.SaveChangesAsync(ct);
    }
}
