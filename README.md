## Live Demo: https://socialhub-web-kindi-h2ckgmgrftere8fa.westus3-01.azurewebsites.net/

# socialhub
=======
# SocialHub

A Facebook-style social network built with **ASP.NET Core 10**, **Entity Framework Core**, **ASP.NET Core Identity**, **JWT Bearer authentication** and **Blazor Server**, with real-time chat and notifications over **SignalR**.

## Features

- **Accounts:** sign up, log in, log out. Passwords are hashed with Identity, and repeated failed logins lock the account for 5 minutes. Sign-in endpoints are rate-limited.
- **JWT with refresh tokens:** access tokens last 15 minutes. Refresh tokens are rotated on every use and stored only as SHA-256 hashes. Reusing an old refresh token revokes all of that user's sessions.
- **Profiles:** name, bio, avatar upload, friend and post counts.
- **Posts:** text and/or a photo, edit, delete, likes, comments.
- **News feed:** your posts plus your friends' posts, with keyset ("load more") pagination.
- **Friends:** send, accept, decline and cancel requests, unfriend, and suggestions ranked by mutual friends.
- **Real-time messenger:** 1:1 chat with typing indicators, read receipts ("Seen") and unread badges.
- **Real-time notifications:** likes, comments and friend requests appear instantly as a badge and a toast.
- **People search.**

## Architecture

```
SocialHub.slnx
├── src/SocialHub.Shared   DTOs and enums shared by API and UI
├── src/SocialHub.Api      ASP.NET Core Web API
│   ├── Controllers/       Auth, Users, Posts, Friends, Messages, Notifications
│   ├── Data/              AppDbContext (IdentityDbContext), entities, seeding
│   ├── Hubs/RealtimeHub   SignalR hub, JWT-authenticated
│   └── Services/          TokenService, NotificationService, FileStorageService, FriendService
└── src/SocialHub.Web      Blazor Web App (Interactive Server)
    ├── Services/          ApiClient (JWT + auto refresh), TokenStore, JwtAuthStateProvider, RealtimeService
    └── Components/        Layout, pages and shared components
```

**Request flow**

```
Browser ⇄ (SignalR circuit) ⇄ SocialHub.Web ──HTTPS + Bearer JWT──▶ SocialHub.Api ──EF Core──▶ SQL Server
                                     └──────── SignalR client (JWT) ───────▶ /hubs/social
```

Design decisions worth talking about in an interview:

- **The API is the security boundary.** Every endpoint requires a valid JWT and checks ownership (only authors can edit or delete posts, only the addressee can accept a request, and so on). The Blazor app is just a client, and the same API could serve a mobile app.
- **Tokens in Blazor Server.** Tokens are held per circuit in `TokenStore` and persisted in the browser through `ProtectedLocalStorage`, which encrypts them with Data Protection, so JavaScript never sees a readable token. `ApiClient` refreshes the token 30 seconds before it expires, and once more after a `401`, using a lock so parallel requests don't all refresh at once.
- **Why the token logic isn't in a `DelegatingHandler`.** `IHttpClientFactory` handlers run in their own DI scope, so they can't see the circuit's scoped `TokenStore`. The typed `ApiClient` does it instead.
- **SignalR + JWT.** WebSockets can't send headers, so the hub reads `access_token` from the query string. A custom `IUserIdProvider` maps the `sub` claim, which means `Clients.User(id)` reaches every tab that user has open.
- **SQL Server cascade rules.** Most tables reference `AspNetUsers` more than once, and SQL Server rejects multiple cascade paths. Only "owned" children (a post's comments and likes) cascade; the rest is cleaned up explicitly.
- **Upload safety.** Images are checked by extension *and* magic bytes, limited to 5 MB, and saved under random names.

## Running it

**Requirements:** .NET 10 SDK and SQL Server. The connection string targets `localhost\SQLEXPRESS` with Windows authentication, like your other projects. Change `ConnectionStrings:SocialHubDb` in `src/SocialHub.Api/appsettings.json` if yours differs; for LocalDB use `Server=(localdb)\\mssqllocaldb;Database=SocialHubDB;Trusted_Connection=True;TrustServerCertificate=True`.

### Visual Studio

1. Open `SocialHub.slnx`.
2. Choose the **API + Web** launch profile in the toolbar (from `SocialHub.slnLaunch`). If it isn't listed, right-click the solution → **Configure Startup Projects** → **Multiple startup projects**, and set both `SocialHub.Api` and `SocialHub.Web` to **Start**.
3. Press **F5**. The browser opens the Web app at https://localhost:7202.

### Command line

```powershell
# terminal 1
cd src/SocialHub.Api
dotnet run --launch-profile https      # https://localhost:7201

# terminal 2
cd src/SocialHub.Web
dotnet run --launch-profile https      # https://localhost:7202
```

On first start the API creates the `SocialHubDB` database and seeds demo data (Development only).

**Demo accounts** (password `Demo@12345`): `alice@demo.local`, `bob@demo.local`, `carol@demo.local`, `dave@demo.local`. Log in as Alice in one browser and Bob in a private window to watch chat and notifications arrive live.

API description (Development): https://localhost:7201/openapi/v1.json

## Moving to EF Core migrations

The first run uses `EnsureCreated` so the app works immediately. To manage schema changes with migrations:

```powershell
dotnet tool install --global dotnet-ef        # once
# drop the auto-created database first (SSMS, or: dotnet ef database drop -p src/SocialHub.Api)
dotnet ef migrations add InitialCreate -p src/SocialHub.Api
```

On the next start, `DbInitializer` detects the migrations and applies them with `MigrateAsync`.

## Configuration and secrets

`appsettings.Development.json` contains a **development-only** JWT signing key. For anything else, set a real one outside source control:

```powershell
cd src/SocialHub.Api
dotnet user-secrets set "Jwt:SigningKey" "<at least 32 random characters>"
```

In production, use environment variables or a key vault (`Jwt__SigningKey`). Point `Api:BaseUrl` in the Web app at the deployed API.

## Ideas for next steps

- Unit and integration tests (xUnit + `WebApplicationFactory` + Testcontainers for SQL Server)
- Groups, shared posts, @mentions, and emoji reactions beyond "like"
- Online presence indicators (track connections in the hub)
- Image storage in Azure Blob Storage, with thumbnails
- Docker Compose (API, Web, SQL Server) and a GitHub Actions CI pipeline
- Deployment to Azure App Service with Azure SQL
>>>>>>> 9e1f53e (SocialHub base-line)
