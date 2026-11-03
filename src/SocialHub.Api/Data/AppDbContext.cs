using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SocialHub.Api.Data.Entities;

namespace SocialHub.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<AppUser>(options)
{
    public DbSet<Post> Posts => Set<Post>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<PostLike> PostLikes => Set<PostLike>();
    public DbSet<Friendship> Friendships => Set<Friendship>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // SQL Server rejects multiple cascade paths to the same table, and almost every
        // table here points at AspNetUsers more than once. Cascades are therefore limited to
        // "owned" children (a post's comments and likes); everything else is Restrict/NoAction
        // and cleaned up explicitly in code.

        b.Entity<AppUser>(e =>
        {
            e.Property(u => u.DisplayName).HasMaxLength(60).IsRequired();
            e.Property(u => u.Bio).HasMaxLength(500);
            e.Property(u => u.AvatarUrl).HasMaxLength(300);
            e.HasIndex(u => u.DisplayName);
        });

        b.Entity<Post>(e =>
        {
            e.Property(p => p.Content).HasMaxLength(5000).IsRequired();
            e.Property(p => p.ImageUrl).HasMaxLength(300);
            e.HasOne(p => p.Author).WithMany(u => u.Posts)
                .HasForeignKey(p => p.AuthorId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(p => new { p.AuthorId, p.Id });
        });

        b.Entity<Comment>(e =>
        {
            e.Property(c => c.Content).HasMaxLength(2000).IsRequired();
            e.HasOne(c => c.Post).WithMany(p => p.Comments)
                .HasForeignKey(c => c.PostId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(c => c.Author).WithMany()
                .HasForeignKey(c => c.AuthorId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<PostLike>(e =>
        {
            e.HasKey(l => new { l.PostId, l.UserId });
            e.HasOne(l => l.Post).WithMany(p => p.Likes)
                .HasForeignKey(l => l.PostId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(l => l.User).WithMany()
                .HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Friendship>(e =>
        {
            e.HasOne(f => f.Requester).WithMany()
                .HasForeignKey(f => f.RequesterId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(f => f.Addressee).WithMany()
                .HasForeignKey(f => f.AddresseeId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(f => new { f.RequesterId, f.AddresseeId }).IsUnique();
            e.HasIndex(f => new { f.AddresseeId, f.Status });
        });

        b.Entity<Message>(e =>
        {
            e.Property(m => m.Content).HasMaxLength(4000).IsRequired();
            e.HasOne(m => m.Sender).WithMany()
                .HasForeignKey(m => m.SenderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(m => m.Recipient).WithMany()
                .HasForeignKey(m => m.RecipientId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(m => new { m.SenderId, m.RecipientId, m.Id });
            e.HasIndex(m => new { m.RecipientId, m.ReadAt });
        });

        b.Entity<Notification>(e =>
        {
            e.HasOne(n => n.User).WithMany()
                .HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(n => n.Actor).WithMany()
                .HasForeignKey(n => n.ActorId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(n => new { n.UserId, n.IsRead, n.Id });
        });

        b.Entity<RefreshToken>(e =>
        {
            e.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
            e.Property(t => t.ReplacedByTokenHash).HasMaxLength(64);
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasOne(t => t.User).WithMany()
                .HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Ignore(t => t.IsActive);
        });
    }
}
