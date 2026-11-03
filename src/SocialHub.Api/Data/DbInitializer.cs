using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SocialHub.Api.Data.Entities;

namespace SocialHub.Api.Data;

public static class DbInitializer
{
    public const string DemoPassword = "Demo@12345";

    /// <summary>
    /// Applies migrations when the project has them; otherwise creates the schema directly so
    /// the app runs on first launch. Run "dotnet ef migrations add InitialCreate" once you want
    /// to manage schema changes with migrations (drop the database first if it was auto-created).
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider services, bool seedDemoData)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DbInitializer");

        if (db.Database.GetMigrations().Any())
        {
            await db.Database.MigrateAsync();
        }
        else
        {
            logger.LogWarning("No EF Core migrations found; creating the schema with EnsureCreated.");
            await db.Database.EnsureCreatedAsync();
        }

        if (!seedDemoData || await db.Users.AnyAsync()) return;

        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var alice = await CreateUser(users, "alice@demo.local", "Alice Johnson",
            "Coffee, code and long hikes. Building things with .NET.");
        var bob = await CreateUser(users, "bob@demo.local", "Bob Smith", "Photographer. Desert sunsets are my thing.");
        var carol = await CreateUser(users, "carol@demo.local", "Carol Nguyen", "Product designer and plant parent.");
        var dave = await CreateUser(users, "dave@demo.local", "Dave Patel", "Just joined!");

        db.Friendships.AddRange(
            new Friendship { RequesterId = alice.Id, AddresseeId = bob.Id, Status = FriendshipStatus.Accepted, RespondedAt = DateTime.UtcNow },
            new Friendship { RequesterId = carol.Id, AddresseeId = alice.Id, Status = FriendshipStatus.Accepted, RespondedAt = DateTime.UtcNow },
            new Friendship { RequesterId = bob.Id, AddresseeId = carol.Id, Status = FriendshipStatus.Accepted, RespondedAt = DateTime.UtcNow },
            new Friendship { RequesterId = dave.Id, AddresseeId = alice.Id, Status = FriendshipStatus.Pending });

        var now = DateTime.UtcNow;
        var p1 = new Post { AuthorId = bob.Id, Content = "Caught the sunset from South Mountain tonight. Worth the climb!", CreatedAt = now.AddHours(-20) };
        var p2 = new Post { AuthorId = carol.Id, Content = "Redesigned our onboarding flow this week. Fewer steps, happier users.", CreatedAt = now.AddHours(-8) };
        var p3 = new Post { AuthorId = alice.Id, Content = "Just shipped real-time chat with SignalR and Blazor Server. Loving this stack.", CreatedAt = now.AddHours(-2) };
        db.Posts.AddRange(p1, p2, p3);
        await db.SaveChangesAsync();

        db.PostLikes.AddRange(
            new PostLike { PostId = p1.Id, UserId = alice.Id },
            new PostLike { PostId = p1.Id, UserId = carol.Id },
            new PostLike { PostId = p3.Id, UserId = bob.Id });
        db.Comments.AddRange(
            new Comment { PostId = p1.Id, AuthorId = alice.Id, Content = "Stunning shot!", CreatedAt = now.AddHours(-19) },
            new Comment { PostId = p3.Id, AuthorId = carol.Id, Content = "Nice! Can I see a demo?", CreatedAt = now.AddHours(-1) });
        db.Messages.AddRange(
            new Message { SenderId = bob.Id, RecipientId = alice.Id, Content = "Hey Alice, are we still on for Saturday?", SentAt = now.AddMinutes(-45) },
            new Message { SenderId = alice.Id, RecipientId = bob.Id, Content = "Yes! 9am at the trailhead.", SentAt = now.AddMinutes(-40), ReadAt = now.AddMinutes(-39) });
        await db.SaveChangesAsync();

        logger.LogInformation("Seeded demo users (password {Password}): alice@, bob@, carol@, dave@demo.local", DemoPassword);
    }

    private static async Task<AppUser> CreateUser(UserManager<AppUser> users, string email, string name, string bio)
    {
        var user = new AppUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = name, Bio = bio };
        var result = await users.CreateAsync(user, DemoPassword);
        if (!result.Succeeded)
            throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));
        return user;
    }
}
