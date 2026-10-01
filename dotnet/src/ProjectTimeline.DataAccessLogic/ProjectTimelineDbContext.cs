using Microsoft.EntityFrameworkCore;

namespace ProjectTimeline.DataAccessLogic;

public sealed class ProjectTimelineDbContext(DbContextOptions<ProjectTimelineDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>(); public DbSet<UserRole> UserRoles => Set<UserRole>(); public DbSet<Project> Projects => Set<Project>(); public DbSet<ProjectParticipant> ProjectParticipants => Set<ProjectParticipant>(); public DbSet<ProjectRole> ProjectRoles => Set<ProjectRole>(); public DbSet<Timeline> Timelines => Set<Timeline>(); public DbSet<TimelineEntry> TimelineEntries => Set<TimelineEntry>(); public DbSet<TimelineArtifact> TimelineArtifacts => Set<TimelineArtifact>(); public DbSet<MergeRequest> MergeRequests => Set<MergeRequest>(); public DbSet<PasswordRecoveryState> PasswordRecoveryStates => Set<PasswordRecoveryState>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        foreach (var type in new[] { typeof(User), typeof(UserRole), typeof(Project), typeof(ProjectParticipant), typeof(ProjectRole), typeof(Timeline), typeof(TimelineEntry), typeof(TimelineArtifact), typeof(MergeRequest), typeof(PasswordRecoveryState) }) b.Entity(type).Property<DateTime>(nameof(AuditedEntity.CreatedAtUtc)).IsRequired();
        b.Entity<User>().Property(x => x.Email).HasMaxLength(320).IsRequired(); b.Entity<User>().HasIndex(x => x.Email).IsUnique(); b.Entity<User>().Property(x => x.DisplayName).HasMaxLength(160).IsRequired(); b.Entity<User>().Property(x => x.PasswordHash).HasMaxLength(512).IsRequired();
        b.Entity<UserRole>().Property(x => x.RoleName).HasMaxLength(40).IsRequired(); b.Entity<UserRole>().HasIndex(x => new { x.UserId, x.RoleName }).IsUnique();
        b.Entity<Project>().Property(x => x.Code).HasMaxLength(32).IsRequired(); b.Entity<Project>().HasIndex(x => x.Code).IsUnique(); b.Entity<Project>().Property(x => x.Name).HasMaxLength(160).IsRequired(); b.Entity<Project>().Property(x => x.Description).HasMaxLength(2000);
        b.Entity<ProjectParticipant>().Property(x => x.ProjectRoleName).HasMaxLength(40).IsRequired(); b.Entity<ProjectParticipant>().HasIndex(x => new { x.ProjectId, x.UserId }).IsUnique();
        b.Entity<ProjectRole>().Property(x => x.Name).HasMaxLength(40).IsRequired(); b.Entity<ProjectRole>().HasIndex(x => new { x.ProjectId, x.Name }).IsUnique();
        b.Entity<Timeline>().Property(x => x.Code).HasMaxLength(32).IsRequired(); b.Entity<Timeline>().HasIndex(x => new { x.ProjectId, x.Code }).IsUnique(); b.Entity<Timeline>().Property(x => x.Name).HasMaxLength(160).IsRequired();
        b.Entity<TimelineEntry>().Property(x => x.Title).HasMaxLength(200).IsRequired(); b.Entity<TimelineEntry>().Property(x => x.Content).HasMaxLength(20000).IsRequired(); b.Entity<TimelineEntry>().Property(x => x.Status).HasMaxLength(30).IsRequired(); b.Entity<TimelineEntry>().HasIndex(x => new { x.TimelineId, x.EntryNumber }).IsUnique();
        b.Entity<TimelineArtifact>().Property(x => x.FileName).HasMaxLength(255).IsRequired(); b.Entity<TimelineArtifact>().Property(x => x.RelativePath).HasMaxLength(1024).IsRequired();
        b.Entity<MergeRequest>().Property(x => x.Summary).HasMaxLength(4000).IsRequired(); b.Entity<MergeRequest>().Property(x => x.Status).HasMaxLength(30).IsRequired();
        b.Entity<PasswordRecoveryState>().Property(x => x.TokenHash).HasMaxLength(128).IsRequired(); b.Entity<PasswordRecoveryState>().HasIndex(x => x.TokenHash).IsUnique(); b.Entity<PasswordRecoveryState>().Property(x => x.ExpiresAtUtc).IsRequired();
        b.Entity<Project>().HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict); b.Entity<TimelineEntry>().HasOne(x => x.Author).WithMany().HasForeignKey(x => x.AuthorId).OnDelete(DeleteBehavior.Restrict); b.Entity<MergeRequest>().HasOne(x => x.Author).WithMany().HasForeignKey(x => x.AuthorId).OnDelete(DeleteBehavior.Restrict);
    }
    public override int SaveChanges() { Stamp(); return base.SaveChanges(); } public override Task<int> SaveChangesAsync(CancellationToken ct = default) { Stamp(); return base.SaveChangesAsync(ct); }
    private void Stamp() { var now = DateTime.UtcNow; foreach (var e in ChangeTracker.Entries<AuditedEntity>()) { if (e.State == EntityState.Added) e.Entity.CreatedAtUtc = now; if (e.State is EntityState.Added or EntityState.Modified) e.Entity.UpdatedAtUtc = now; } }
}
