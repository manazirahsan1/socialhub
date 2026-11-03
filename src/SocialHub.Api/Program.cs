using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SocialHub.Api.Data;
using SocialHub.Api.Data.Entities;
using SocialHub.Api.Hubs;
using SocialHub.Api.Infrastructure;
using SocialHub.Api.Services;
using SocialHub.Shared;

var builder = WebApplication.CreateBuilder(args);

// ---------- Configuration ----------
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwt.SigningKey) || jwt.SigningKey.Length < 32)
    throw new InvalidOperationException(
        "Jwt:SigningKey must be at least 32 characters. Set it with: dotnet user-secrets set \"Jwt:SigningKey\" \"<long random value>\"");

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
builder.Services.Configure<UploadOptions>(builder.Configuration.GetSection("Uploads"));

// ---------- Data + Identity ----------
// builder.Services.AddDbContext<AppDbContext>(o =>
//     o.UseSqlServer(builder.Configuration.GetConnectionString("SocialHubDb")));
	
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("SocialHubDb"),
        sql => sql.EnableRetryOnFailure()));

builder.Services.AddIdentityCore<AppUser>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 8;
        o.Password.RequireNonAlphanumeric = false;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
        o.Lockout.AllowedForNewUsers = true;
    })
    .AddEntityFrameworkStores<AppDbContext>();

// ---------- JWT Bearer authentication ----------
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false; // keep "sub", "name" etc. as-is
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = TokenService.SigningKey(jwt),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = AppClaims.Name
        };

        // WebSockets can't send an Authorization header, so SignalR passes the token in the query string.
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/" + HubEvents.HubPath))
                    ctx.Token = token;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

// Throttle sign-in and registration to slow down password guessing.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
});

// ---------- App services ----------
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<FriendService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<FileStorageService>();

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddSignalR();
builder.Services.AddSingleton<IUserIdProvider, SubClaimUserIdProvider>();

var app = builder.Build();

// await DbInitializer.InitializeAsync(app.Services,
//     seedDemoData: app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("SeedDemoData"));

await DbInitializer.InitializeAsync(app.Services,
    seedDemoData: app.Configuration.GetValue<bool>("SeedDemoData"));

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi(); // /openapi/v1.json
}
else
{
    app.UseExceptionHandler();
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles(); // serves /uploads/*
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<RealtimeHub>("/" + HubEvents.HubPath);
app.MapGet("/", () => Results.Ok(new { name = "SocialHub API", docs = "/openapi/v1.json" })).AllowAnonymous();

app.Run();
