using Microsoft.EntityFrameworkCore;

public sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Member> Members => Set<Member>();
    public DbSet<WorkItem> Tasks => Set<WorkItem>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Account>().HasIndex(x => x.Login).IsUnique();
        b.Entity<Account>().Property(x => x.Login).HasMaxLength(32);
        b.Entity<Session>().HasKey(x => x.TokenHash);
        b.Entity<Session>().HasOne<Account>().WithMany().HasForeignKey(x => x.UserId);
        b.Entity<Team>().HasOne<Account>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Team>().Property(x => x.Name).HasMaxLength(100);
        b.Entity<Member>().HasKey(x => new { x.TeamId, x.UserId });
        b.Entity<Member>().HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId);
        b.Entity<Member>().HasOne<Account>().WithMany().HasForeignKey(x => x.UserId);
        b.Entity<WorkItem>().HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<WorkItem>().HasOne<Account>().WithMany().HasForeignKey(x => x.AuthorId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<WorkItem>().HasOne<Account>().WithMany().HasForeignKey(x => x.AssigneeId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<WorkItem>().Property(x => x.Version).IsConcurrencyToken();
        b.Entity<WorkItem>().Property(x => x.Title).HasMaxLength(200);
        b.Entity<WorkItem>().Property(x => x.Description).HasMaxLength(4000);
        b.Entity<WorkItem>().Property(x => x.Result).HasMaxLength(8000);
    }
}
public sealed class Account
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Login { get; set; } = "";
    public string PasswordHash { get; set; } = "";
}
public sealed class Session
{
    public string TokenHash { get; set; } = "";
    public Guid UserId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
public sealed class Team
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public Guid OwnerId { get; set; }
}
public sealed class Member
{
    public Guid TeamId { get; set; }
    public Guid UserId { get; set; }
}
public sealed class WorkItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TeamId { get; set; }
    public Guid AuthorId { get; set; }
    public Guid AssigneeId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Result { get; set; } = "";
    public string State { get; set; } = "assigned";
    public int Version { get; set; } = 1;
}
public record Credentials(string? Login, string? Password);
public record NewTeam(string? Name);
public record NewMember(Guid UserId);
public record NewTask(Guid AssigneeId, string? Title, string? Description);
public record Submission(string? Result, int Version);
public record Review(int Version);
