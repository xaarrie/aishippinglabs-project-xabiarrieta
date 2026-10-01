using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.BusinessLogic;

public sealed record LoginResult(User User, string TokenSubject);
public static class PasswordService
{
    public static string Hash(string password) { var salt = RandomNumberGenerator.GetBytes(16); var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32); return $"v1.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}"; }
    public static bool Verify(string password, string encoded) { try { var p = encoded.Split('.'); if (p.Length != 3) return false; var key = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(p[1]), 120_000, HashAlgorithmName.SHA256, 32); return CryptographicOperations.FixedTimeEquals(key, Convert.FromBase64String(p[2])); } catch { return false; } }
}
public sealed class TimelineService(ProjectTimelineDbContext db)
{
    public async Task<TimelineEntry> AddEntryAsync(Guid timelineId, Guid authorId, string title, string content, CancellationToken ct)
    {
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct) : null;
        var timeline = await db.Timelines.SingleOrDefaultAsync(x => x.Id == timelineId, ct) ?? throw new KeyNotFoundException("Timeline not found.");
        var number = (await db.TimelineEntries.Where(x => x.TimelineId == timelineId).MaxAsync(x => (int?)x.EntryNumber, ct) ?? 0) + 1;
        var entry = new TimelineEntry { TimelineId = timeline.Id, AuthorId = authorId, EntryNumber = number, Title = title.Trim(), Content = content.Trim() };
        if (entry.Title.Length is < 1 or > 200 || entry.Content.Length > 20000) throw new ArgumentException("Entry title or content is invalid.");
        db.TimelineEntries.Add(entry); await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct); return entry;
    }
    public async Task<bool> IsProjectOwnerAsync(Guid projectId, Guid userId, CancellationToken ct) => await db.Projects.AnyAsync(x => x.Id == projectId && x.OwnerId == userId, ct);
}
public sealed class ProjectService(ProjectTimelineDbContext db)
{
    public async Task<Project> CreateAsync(Guid ownerId, string code, string name, string? description, CancellationToken ct) { if (!await db.Users.AnyAsync(x => x.Id == ownerId, ct)) throw new KeyNotFoundException("Owner not found."); var p = new Project { OwnerId = ownerId, Code = code.Trim().ToUpperInvariant(), Name = name.Trim(), Description = description?.Trim() }; p.Participants.Add(new ProjectParticipant { UserId = ownerId, ProjectRoleName = "Creator" }); foreach (var role in new[] { "Creator", "Reviewer", "Contributor" }) p.Roles.Add(new ProjectRole { Name = role }); db.Projects.Add(p); await db.SaveChangesAsync(ct); return p; }
}
