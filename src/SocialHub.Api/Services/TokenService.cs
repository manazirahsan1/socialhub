using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SocialHub.Api.Data;
using SocialHub.Api.Data.Entities;
using SocialHub.Api.Infrastructure;
using SocialHub.Shared;

namespace SocialHub.Api.Services;

public class TokenService(AppDbContext db, IOptions<JwtOptions> options, ILogger<TokenService> logger)
{
    private readonly JwtOptions _opt = options.Value;

    /// <summary>Issues a new access token and a new refresh token for the user.</summary>
    public async Task<AuthResponse> IssueAsync(AppUser user, CancellationToken ct = default)
    {
        var (raw, entity) = NewRefreshToken(user.Id);
        db.RefreshTokens.Add(entity);
        await db.SaveChangesAsync(ct);
        return BuildResponse(user, raw);
    }

    /// <summary>
    /// Exchanges a refresh token for a new pair (rotation). Presenting a token that was
    /// already rotated means it has leaked, so every token for that user is revoked.
    /// </summary>
    public async Task<AuthResponse?> RefreshAsync(string rawToken, CancellationToken ct = default)
    {
        var hash = Hash(rawToken);
        var existing = await db.RefreshTokens.Include(t => t.User)
            .SingleOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (existing is null) return null;

        if (existing.RevokedAt is not null)
        {
            if (existing.ReplacedByTokenHash is not null)
            {
                logger.LogWarning("Refresh token reuse detected for user {UserId}; revoking all sessions.", existing.UserId);
                await RevokeAllAsync(existing.UserId, ct);
            }
            return null;
        }

        if (existing.ExpiresAt <= DateTime.UtcNow) return null;

        var (raw, replacement) = NewRefreshToken(existing.UserId);
        existing.RevokedAt = DateTime.UtcNow;
        existing.ReplacedByTokenHash = replacement.TokenHash;
        db.RefreshTokens.Add(replacement);
        await db.SaveChangesAsync(ct);

        return BuildResponse(existing.User, raw);
    }

    public async Task RevokeAsync(string rawToken, CancellationToken ct = default)
    {
        var hash = Hash(rawToken);
        var token = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is { RevokedAt: null })
        {
            token.RevokedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task RevokeAllAsync(string userId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    private AuthResponse BuildResponse(AppUser user, string rawRefreshToken)
    {
        var expires = DateTime.UtcNow.AddMinutes(_opt.AccessTokenMinutes);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _opt.Issuer,
            Audience = _opt.Audience,
            Expires = expires,
            Subject = new ClaimsIdentity(
            [
                new Claim(AppClaims.UserId, user.Id),
                new Claim(AppClaims.Name, user.DisplayName),
                new Claim(AppClaims.Email, user.Email ?? ""),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            ]),
            SigningCredentials = new SigningCredentials(SigningKey(_opt), SecurityAlgorithms.HmacSha256)
        };

        var accessToken = new JsonWebTokenHandler().CreateToken(descriptor);
        return new AuthResponse(accessToken, new DateTimeOffset(expires, TimeSpan.Zero), rawRefreshToken, user.ToSummary());
    }

    private (string Raw, RefreshToken Entity) NewRefreshToken(string userId)
    {
        var raw = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(64));
        return (raw, new RefreshToken
        {
            UserId = userId,
            TokenHash = Hash(raw),
            ExpiresAt = DateTime.UtcNow.AddDays(_opt.RefreshTokenDays)
        });
    }

    private static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    public static SymmetricSecurityKey SigningKey(JwtOptions opt) =>
        new(Encoding.UTF8.GetBytes(opt.SigningKey));
}
