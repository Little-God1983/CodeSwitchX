using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace CodeSwitchX.Data;

public sealed class CodeSwitchXDbContext : DbContext
{
    public CodeSwitchXDbContext(DbContextOptions<CodeSwitchXDbContext> options)
        : base(options)
    {
    }

    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Worktree> Worktrees => Set<Worktree>();
    public DbSet<Track> Tracks => Set<Track>();
    public DbSet<SessionRecord> Sessions => Set<SessionRecord>();
    public DbSet<SessionEventRecord> SessionEvents => Set<SessionEventRecord>();
    public DbSet<UsageBucket> UsageBuckets => Set<UsageBucket>();
    public DbSet<TranscriptCursor> TranscriptCursors => Set<TranscriptCursor>();
    public DbSet<SeenMessage> SeenMessages => Set<SeenMessage>();
    public DbSet<PricingRule> PricingRules => Set<PricingRule>();
    public DbSet<Setting> Settings => Set<Setting>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<UtcTicksConverter>();
        configurationBuilder.Properties<decimal>().HaveConversion<double>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Track>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Name).IsRequired().HasMaxLength(200);
        });

        modelBuilder.Entity<Workspace>(e =>
        {
            e.HasKey(w => w.Id);
            e.Property(w => w.Name).IsRequired().HasMaxLength(200);
            e.Property(w => w.RootPath).IsRequired().HasMaxLength(1024);
            e.HasIndex(w => w.RootPath).IsUnique();
            e.Property(w => w.AccentColor).IsRequired().HasMaxLength(16);
            e.Property(w => w.HostMode).HasConversion<string>().HasMaxLength(16);
            e.HasOne<Track>().WithMany().HasForeignKey(w => w.TrackId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(w => w.Worktrees).WithOne().HasForeignKey(t => t.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(w => w.Worktrees).AutoInclude();
        });

        modelBuilder.Entity<Worktree>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Path).IsRequired().HasMaxLength(1024);
            e.HasIndex(t => t.Path);
        });

        modelBuilder.Entity<SessionRecord>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasMaxLength(128);
            e.Property(s => s.State).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(s => s.WorkspaceId);
            e.HasIndex(s => s.LastEventAt);
        });

        modelBuilder.Entity<SessionEventRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.SessionId).IsRequired().HasMaxLength(128);
            e.Property(x => x.Kind).IsRequired().HasMaxLength(64);
            e.HasIndex(x => new { x.SessionId, x.At });
            e.HasIndex(x => x.At);
        });

        modelBuilder.Entity<UsageBucket>(e =>
        {
            e.HasKey(b => new { b.SessionId, b.Model, b.MinuteUtc });
            e.Property(b => b.SessionId).HasMaxLength(128);
            e.Property(b => b.Model).HasMaxLength(128);
            e.HasIndex(b => b.MinuteUtc);
        });

        modelBuilder.Entity<TranscriptCursor>(e =>
        {
            e.HasKey(c => c.Path);
            e.Property(c => c.Path).HasMaxLength(1024);
        });

        modelBuilder.Entity<SeenMessage>(e =>
        {
            e.HasKey(m => m.Seq);
            e.Property(m => m.Seq).ValueGeneratedNever();
            e.Property(m => m.MessageId).IsRequired().HasMaxLength(128);
            e.HasIndex(m => m.MessageId).IsUnique();
        });

        modelBuilder.Entity<PricingRule>(e =>
        {
            e.HasKey(p => p.Model);
            e.Property(p => p.Model).HasMaxLength(128);
        });

        modelBuilder.Entity<Setting>(e =>
        {
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasMaxLength(128);
            e.Property(s => s.ValueJson).IsRequired();
        });
    }
}
