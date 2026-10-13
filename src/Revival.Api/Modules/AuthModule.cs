using System.Text.Json;
using System.Text.RegularExpressions;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Revival.Api.Data;

namespace Revival.Api.Modules;

public static partial class AuthModule
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/v1/auth").RequireRateLimiting("auth");
        auth.MapPost("/register", RegisterAsync);
        auth.MapPost("/login", LoginAsync);
        auth.MapPost("/refresh", RefreshAsync);

        var signedIn = app.MapGroup("/v1/auth").AddEndpointFilter<PlayerAuthFilter>();
        signedIn.MapGet("/me", (HttpContext http) => Results.Ok(SessionService.PublicPlayer(http.CurrentPlayer())));
        signedIn.MapPost("/logout", LogoutAsync);
    }

    private static async Task<IResult> RegisterAsync(RegisterRequest request, AppDbContext db, SessionService sessions, CancellationToken ct)
    {
        var userName = (request.UserName ?? request.DisplayName ?? "").Trim();
        var displayName = (request.DisplayName ?? userName).Trim();
        if (!UserNamePattern().IsMatch(userName)) return Results.BadRequest(new { error = "invalid_user_name", message = "User names must be 3-32 characters using letters, numbers, dot, dash, or underscore." });
        if (displayName.Length is < 2 or > 48) return Results.BadRequest(new { error = "invalid_display_name", message = "Display names must be 2-48 characters." });
        if (request.Password is null || request.Password.Length is < 10 or > 256)
            return Results.BadRequest(new { error = "invalid_password", message = "Password must be 10-256 characters." });

        var normalized = userName.ToUpperInvariant();
        if (await db.Players.AnyAsync(p => p.UserNameNormalized == normalized, ct))
            return Results.Conflict(new { error = "user_name_taken" });

        var player = new Player
        {
            Id = Guid.NewGuid(), UserName = userName, UserNameNormalized = normalized, DisplayName = displayName,
            PasswordHash = PasswordHasher.Hash(request.Password), CreatedAt = DateTimeOffset.UtcNow,
            ProgressionJson = JsonSerializer.Serialize(new { level = 1, experience = 0, coins = 0 })
        };
        db.Players.Add(player);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return Results.Conflict(new { error = "user_name_taken" }); }
        return Results.Created($"/v1/players/{player.Id}", await sessions.CreateAsync(player, ct));
    }

    private static async Task<IResult> LoginAsync(LoginRequest request, HttpContext http, AppDbContext db, SessionService sessions, CancellationToken ct)
    {
        var userName = (request.UserName ?? request.DisplayName ?? "").Trim();
        if (userName.Length is < 3 or > 32 || string.IsNullOrEmpty(request.Password))
            return Results.Unauthorized();
        var normalized = userName.ToUpperInvariant();
        var now = DateTimeOffset.UtcNow;
        var attempt = await db.LoginAttempts.FindAsync([normalized], ct);
        if (attempt?.LockedUntil is not null && attempt.LockedUntil > now)
        {
            http.Response.Headers.RetryAfter = Math.Max(1, (int)(attempt.LockedUntil.Value - now).TotalSeconds).ToString();
            return Results.Json(new { error = "account_temporarily_locked" }, statusCode: StatusCodes.Status429TooManyRequests);
        }
        var player = await db.Players.FirstOrDefaultAsync(p => p.UserNameNormalized == normalized, ct);
        if (player is null || !PasswordHasher.Verify(request.Password, player.PasswordHash))
        {
            if (attempt is null)
            {
                attempt = new LoginAttempt { UserNameNormalized = normalized, FailureCount = 0, WindowStartedAt = now };
                db.LoginAttempts.Add(attempt);
            }
            if (now - attempt.WindowStartedAt > TimeSpan.FromMinutes(15)) { attempt.FailureCount = 0; attempt.WindowStartedAt = now; attempt.LockedUntil = null; }
            attempt.FailureCount++;
            if (attempt.FailureCount >= 8) attempt.LockedUntil = now.AddMinutes(15);
            await db.SaveChangesAsync(ct);
            return Results.Unauthorized();
        }
        if (attempt is not null) db.LoginAttempts.Remove(attempt);
        if (player.IsSuspended && (player.SuspendedUntil is null || player.SuspendedUntil > DateTimeOffset.UtcNow))
            return Results.Json(new { error = "account_suspended", until = player.SuspendedUntil }, statusCode: StatusCodes.Status403Forbidden);
        return Results.Ok(await sessions.CreateAsync(player, ct));
    }

    private static async Task<IResult> RefreshAsync(RefreshRequest request, AppDbContext db, SessionService sessions, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken)) return Results.BadRequest(new { error = "refresh_token_required" });
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var old = await sessions.FindRefreshSessionAsync(request.RefreshToken, ct);
        if (old is null) return Results.Unauthorized();
        var now = DateTimeOffset.UtcNow;
        if (old.RevokedAt is not null || old.RefreshExpiresAt <= now)
        {
            // Reuse of a rotated token is treated as possible theft; revoke all sessions for that account.
            if (old.RevokedAt is not null)
            {
                await db.AuthSessions.Where(s => s.PlayerId == old.PlayerId && s.RevokedAt == null)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.RevokedAt, now), ct);
                await transaction.CommitAsync(ct);
            }
            return Results.Unauthorized();
        }
        var player = await db.Players.FindAsync([old.PlayerId], ct);
        if (player is null || (player.IsSuspended && (player.SuspendedUntil is null || player.SuspendedUntil > now))) return Results.Unauthorized();
        var claimed = await db.AuthSessions.Where(s => s.Id == old.Id && s.RevokedAt == null && s.RefreshExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.RevokedAt, now), ct);
        if (claimed != 1) return Results.Unauthorized();
        old.RevokedAt = now;
        var response = await sessions.CreateAsync(player, ct);
        var replacementHash = SessionService.HashToken(response.RefreshToken);
        var replacement = await db.AuthSessions.FirstAsync(s => s.RefreshTokenHash == replacementHash, ct);
        old.ReplacedBySessionId = replacement.Id;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(response);
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (http.Items.TryGetValue(AuthContextKeys.Session, out var value) && value is AuthSession session)
        {
            session.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return Results.NoContent();
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]{3,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex UserNamePattern();
}

public sealed record RegisterRequest(string? UserName, string? DisplayName, string? Password);
public sealed record LoginRequest(string? UserName, string? Password, string? DisplayName = null);
public sealed record RefreshRequest(string? RefreshToken);

public static class ProfileModule
{
    public static void MapProfileEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/v1/players").AddEndpointFilter<PlayerAuthFilter>();
        group.MapGet("/{playerId:guid}", GetProfileAsync);
        group.MapPatch("/{playerId:guid}", UpdateProfileAsync);
        group.MapGet("/{playerId:guid}/settings", GetSettingsAsync);
        group.MapPut("/{playerId:guid}/settings", PutSettingsAsync);
        group.MapGet("/{playerId:guid}/progression", GetPlayerProgressionAsync);
    }

    private static async Task<IResult> GetProfileAsync(Guid playerId, AppDbContext db, CancellationToken ct)
    {
        var player = await db.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == playerId, ct);
        return player is null ? Results.NotFound() : Results.Ok(new
        {
            id = player.Id, userName = player.UserName, displayName = player.DisplayName,
            bio = player.Bio, avatarAssetId = player.AvatarAssetId, createdAt = player.CreatedAt
        });
    }

    private static async Task<IResult> UpdateProfileAsync(Guid playerId, ProfilePatch request, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (playerId != http.PlayerId()) return Results.Forbid();
        var player = await db.Players.FindAsync([playerId], ct);
        if (player is null) return Results.NotFound();
        if (request.DisplayName is not null)
        {
            var value = request.DisplayName.Trim();
            if (value.Length is < 2 or > 48) return Results.BadRequest(new { error = "invalid_display_name" });
            player.DisplayName = value;
        }
        if (request.Bio is not null)
        {
            if (request.Bio.Length > 280) return Results.BadRequest(new { error = "bio_too_long" });
            player.Bio = request.Bio.Trim();
        }
        if (request.AvatarAssetId.HasValue)
        {
            if (!await db.Assets.AnyAsync(a => a.Id == request.AvatarAssetId && !a.IsRevoked && a.OwnerId == playerId, ct))
                return Results.BadRequest(new { error = "avatar_asset_not_owned" });
            player.AvatarAssetId = request.AvatarAssetId;
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { id = player.Id, userName = player.UserName, displayName = player.DisplayName, bio = player.Bio, avatarAssetId = player.AvatarAssetId });
    }

    private static async Task<IResult> GetSettingsAsync(Guid playerId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (playerId != http.PlayerId()) return Results.Forbid();
        var player = await db.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == playerId, ct);
        return player is null ? Results.NotFound() : Results.Content(player.SettingsJson, "application/json");
    }

    private static async Task<IResult> PutSettingsAsync(Guid playerId, JsonElement settings, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (playerId != http.PlayerId()) return Results.Forbid();
        if (settings.ValueKind != JsonValueKind.Object || settings.GetRawText().Length > 16_384)
            return Results.BadRequest(new { error = "settings_must_be_json_object_under_16k" });
        var player = await db.Players.FindAsync([playerId], ct);
        if (player is null) return Results.NotFound();
        player.SettingsJson = settings.GetRawText();
        await db.SaveChangesAsync(ct);
        return Results.Content(player.SettingsJson, "application/json");
    }

    private static async Task<IResult> GetPlayerProgressionAsync(Guid playerId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (playerId != http.PlayerId()) return Results.Forbid();
        var player = await db.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == playerId, ct);
        return player is null ? Results.NotFound() : Results.Content(player.ProgressionJson, "application/json");
    }
}

public sealed record ProfilePatch(string? DisplayName, string? Bio, Guid? AvatarAssetId);
