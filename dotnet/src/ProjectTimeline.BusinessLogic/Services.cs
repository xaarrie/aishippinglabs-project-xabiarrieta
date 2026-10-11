using System.Security.Cryptography;
using System.Text;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.BusinessLogic;

public sealed record LoginResult(User User, string TokenSubject);
public static class PasswordService
{
    public static string Hash(string password) { var salt = RandomNumberGenerator.GetBytes(16); var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32); return $"v1.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}"; }
    public static bool Verify(string password, string encoded) { try { var p = encoded.Split('.'); if (p.Length != 3) return false; var key = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(p[1]), 120_000, HashAlgorithmName.SHA256, 32); return CryptographicOperations.FixedTimeEquals(key, Convert.FromBase64String(p[2])); } catch { return false; } }
}
public interface IRecoveryOutbox { Task DeliverAsync(string recipient, string recoveryLink, CancellationToken ct); }
public sealed class FileRecoveryOutbox(IConfiguration configuration) : IRecoveryOutbox
{
    public async Task DeliverAsync(string recipient, string recoveryLink, CancellationToken ct)
    {
        var directory = configuration["Recovery:OutboxPath"] ?? Path.Combine(AppContext.BaseDirectory, "recovery-outbox");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { recipient, recoveryLink }), ct);
    }
}
public sealed class RecoveryService(ProjectTimelineDbContext db, IRecoveryOutbox outbox, IConfiguration configuration)
{
    public async Task RequestAsync(string email, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(x => x.Email == email.Trim().ToLowerInvariant() && x.IsActive, ct);
        if (user is null) return;
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        db.PasswordRecoveryStates.Add(new PasswordRecoveryState { UserId = user.Id, TokenHash = hash, ExpiresAtUtc = DateTime.UtcNow.AddMinutes(configuration.GetValue("Recovery:LifetimeMinutes", 30)) });
        await db.SaveChangesAsync(ct);
        var baseUrl = configuration["Recovery:BaseUrl"] ?? "http://localhost:5000";
        await outbox.DeliverAsync(user.Email, $"{baseUrl.TrimEnd('/')}/?recoveryToken={Uri.EscapeDataString(token)}", ct);
    }
    public async Task<bool> ResetAsync(string token, string newPassword, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 12) return false;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var state = await db.PasswordRecoveryStates.Include(x => x.User).SingleOrDefaultAsync(x => x.TokenHash == hash && !x.IsUsed, ct);
        if (state is null || state.ExpiresAtUtc <= DateTime.UtcNow || !state.User.IsActive) return false;
        state.User.PasswordHash = PasswordService.Hash(newPassword); state.IsUsed = true;
        await db.SaveChangesAsync(ct); return true;
    }
}
public sealed class ProjectAuthorization(ProjectTimelineDbContext db)
{
    public Task<bool> CanReadAsync(Guid projectId, Guid userId, bool administrator, CancellationToken ct = default) => AccessAsync(projectId, userId, administrator, null, ct);
    public Task<bool> CanWriteAsync(Guid projectId, Guid userId, bool administrator, CancellationToken ct = default) => AccessAsync(projectId, userId, administrator, new[] { "Creator", "Contributor" }, ct);
    public Task<bool> CanManageAsync(Guid projectId, Guid userId, bool administrator, CancellationToken ct = default) => db.Projects.AnyAsync(x => x.Id == projectId && (administrator || x.OwnerId == userId), ct);
    private Task<bool> AccessAsync(Guid projectId, Guid userId, bool administrator, string[]? roles, CancellationToken ct) => db.Projects.AnyAsync(x => x.Id == projectId && (administrator || x.OwnerId == userId || x.Participants.Any(y => y.UserId == userId && (roles == null || roles.Contains(y.ProjectRoleName)))), ct);
}

public sealed class AdminRequirement : Microsoft.AspNetCore.Authorization.IAuthorizationRequirement;
public sealed class CurrentUserAdminHandler(ProjectTimelineDbContext db) : Microsoft.AspNetCore.Authorization.AuthorizationHandler<AdminRequirement>
{
    protected override async Task HandleRequirementAsync(Microsoft.AspNetCore.Authorization.AuthorizationHandlerContext context, AdminRequirement requirement)
    {
        var value = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParse(value, out var userId) && await db.Users.AnyAsync(x => x.Id == userId && x.IsActive && x.UserRole.RoleName == "Admin")) context.Succeed(requirement);
    }
}
public sealed class TimelineService(ProjectTimelineDbContext db)
{
    public static void ValidateEntry(string title, string content, string status)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 200 || string.IsNullOrWhiteSpace(content) || content.Trim().Length > 20000 || string.IsNullOrWhiteSpace(status) || status.Trim().Length > 30) throw new ArgumentException("Entry title, content, or status is invalid.");
    }
    public async Task<TimelineEntry> AddEntryAsync(Guid timelineId, Guid authorId, string title, string content, CancellationToken ct)
    {
        ValidateEntry(title, content, "Draft");
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct) : null;
        var timeline = await db.Timelines.Include(x => x.Project).SingleOrDefaultAsync(x => x.Id == timelineId, ct) ?? throw new KeyNotFoundException("Timeline not found.");
        var author = await db.Users.SingleOrDefaultAsync(x => x.Id == authorId && x.IsActive, ct) ?? throw new KeyNotFoundException("Author not found.");
        var number = (await db.TimelineEntries.Where(x => x.TimelineId == timelineId).MaxAsync(x => (int?)x.EntryNumber, ct) ?? 0) + 1;
        var entry = new TimelineEntry { TimelineId = timeline.Id, Timeline = timeline, AuthorId = author.Id, Author = author, EntryNumber = number, Title = title.Trim(), Content = content.Trim() };
        ValidateEntry(entry.Title, entry.Content, entry.Status);
        db.TimelineEntries.Add(entry); await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct); return entry;
    }
    public async Task<bool> IsProjectOwnerAsync(Guid projectId, Guid userId, CancellationToken ct) => await db.Projects.AnyAsync(x => x.Id == projectId && x.OwnerId == userId, ct);
}
public sealed class ProjectService(ProjectTimelineDbContext db)
{
    public static bool CanRemoveParticipant(Project project, ProjectParticipant participant) => participant.UserId != project.OwnerId;
    public async Task<Project> CreateAsync(Guid ownerId, string code, string name, string? description, CancellationToken ct) { var owner = await db.Users.SingleOrDefaultAsync(x => x.Id == ownerId && x.IsActive, ct) ?? throw new KeyNotFoundException("Owner not found."); var p = new Project { OwnerId = ownerId, Owner = owner, Code = code.Trim().ToUpperInvariant(), Name = name.Trim(), Description = description?.Trim() }; p.Participants.Add(new ProjectParticipant { UserId = ownerId, User = owner, ProjectRoleName = "Creator" }); foreach (var role in new[] { "Creator", "Reviewer", "Contributor" }) p.Roles.Add(new ProjectRole { Name = role }); db.Projects.Add(p); await db.SaveChangesAsync(ct); return p; }
}
