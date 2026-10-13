using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Revival.Api.Data;

namespace Revival.Api.Modules;

public sealed record TokenResponse(string AccessToken, string RefreshToken, string TokenType, DateTimeOffset ExpiresAt, DateTimeOffset RefreshExpiresAt, object Player);

public static class PasswordHasher
{
    private const int Iterations = 240_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var digest = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(digest)}";
    }

    public static bool Verify(string password, string encoded)
    {
        try
        {
            var parts = encoded.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations) || iterations is < 100_000 or > 2_000_000)
                return false;
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }
}

public sealed class SessionService(AppDbContext db)
{
    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async Task<TokenResponse> CreateAsync(Player player, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var access = NewToken();
        var refresh = NewToken();
        var session = new AuthSession
        {
            Id = Guid.NewGuid(), PlayerId = player.Id,
            AccessTokenHash = HashToken(access), RefreshTokenHash = HashToken(refresh),
            CreatedAt = now, AccessExpiresAt = now.AddMinutes(20), RefreshExpiresAt = now.AddDays(30)
        };
        db.AuthSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return new TokenResponse(access, refresh, "Bearer", session.AccessExpiresAt, session.RefreshExpiresAt, PublicPlayer(player));
    }

    public async Task<Player?> AuthenticateAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var token = header[7..].Trim();
        if (token.Length < 32) return null;
        var tokenHash = HashToken(token);
        var now = DateTimeOffset.UtcNow;
        var session = await db.AuthSessions.FirstOrDefaultAsync(s => s.AccessTokenHash == tokenHash
            && s.RevokedAt == null && s.AccessExpiresAt > now, cancellationToken);
        if (session is null) return null;
        var player = await db.Players.FindAsync([session.PlayerId], cancellationToken);
        if (player is null) return null;
        if (player.IsSuspended && (player.SuspendedUntil is null || player.SuspendedUntil > now)) return null;
        if (player.IsSuspended)
        {
            player.IsSuspended = false;
            player.SuspendedUntil = null;
            player.SuspensionReason = null;
            await db.SaveChangesAsync(cancellationToken);
        }
        context.Items[AuthContextKeys.Player] = player;
        context.Items[AuthContextKeys.Session] = session;
        return player;
    }

    public async Task<AuthSession?> FindRefreshSessionAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken) || refreshToken.Length < 32) return null;
        var tokenHash = HashToken(refreshToken);
        return await db.AuthSessions.FirstOrDefaultAsync(s => s.RefreshTokenHash == tokenHash, cancellationToken);
    }

    public static object PublicPlayer(Player player) => new
    {
        id = player.Id, userName = player.UserName, displayName = player.DisplayName,
        createdAt = player.CreatedAt, isAdmin = player.IsAdmin
    };
}

public static class AuthContextKeys
{
    public const string Player = "revival.player";
    public const string Session = "revival.session";

    public static Player CurrentPlayer(this HttpContext context) =>
        context.Items.TryGetValue(Player, out var value) && value is Data.Player player
            ? player : throw new InvalidOperationException("Protected endpoint is missing an authenticated player.");

    public static Guid PlayerId(this HttpContext context) => context.CurrentPlayer().Id;
}

public sealed class PlayerAuthFilter(SessionService sessions) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var player = await sessions.AuthenticateAsync(context.HttpContext, context.HttpContext.RequestAborted);
        if (player is null) return Results.Json(new { error = "unauthorized", message = "A valid, active bearer session is required." }, statusCode: StatusCodes.Status401Unauthorized);
        return await next(context);
    }
}

public sealed class AdminAuthFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var player = context.HttpContext.CurrentPlayer();
        if (!player.IsAdmin)
            return ValueTask.FromResult<object?>(Results.Json(new { error = "forbidden", message = "Administrator permission is required." }, statusCode: StatusCodes.Status403Forbidden));
        return next(context);
    }
}
