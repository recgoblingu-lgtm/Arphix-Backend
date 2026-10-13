using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Revival.Api.Data;

namespace Revival.Api.Modules;

public static class SocialModule
{
    public static void MapSocialEndpoints(this WebApplication app)
    {
        var players = app.MapGroup("/v1/players").AddEndpointFilter<PlayerAuthFilter>();
        players.MapGet("/search", SearchPlayersAsync);
        app.MapGroup("/v1/social").AddEndpointFilter<PlayerAuthFilter>().MapGet("/players", SearchPlayersAsync);
        players.MapPost("/{playerId:guid}/block", BlockAsync);
        players.MapDelete("/{playerId:guid}/block", UnblockAsync);
        players.MapPost("/{playerId:guid}/mute", MuteAsync);
        players.MapDelete("/{playerId:guid}/mute", UnmuteAsync);

        var friends = app.MapGroup("/v1/friends").AddEndpointFilter<PlayerAuthFilter>();
        friends.MapGet("/", ListFriendsAsync);
        friends.MapPost("/requests", SendRequestAsync);
        friends.MapPost("/requests/{requestId:guid}/accept", (Guid requestId, HttpContext http, AppDbContext db, RealtimeEvents events, CancellationToken ct) => RespondAsync(requestId, true, http, db, events, ct));
        friends.MapPost("/requests/{requestId:guid}/decline", (Guid requestId, HttpContext http, AppDbContext db, RealtimeEvents events, CancellationToken ct) => RespondAsync(requestId, false, http, db, events, ct));
        friends.MapDelete("/{playerId:guid}", RemoveFriendAsync);

        var presence = app.MapGroup("/v1/presence").AddEndpointFilter<PlayerAuthFilter>();
        presence.MapGet("/", GetPresenceAsync);

        var notifications = app.MapGroup("/v1/notifications").AddEndpointFilter<PlayerAuthFilter>();
        notifications.MapGet("/", ListNotificationsAsync);
        notifications.MapPost("/{notificationId:guid}/read", MarkNotificationReadAsync);
    }

    private static async Task<IResult> SearchPlayersAsync(string? q, int? limit, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var term = (q ?? "").Trim();
        if (term.Length > 64) return Results.BadRequest(new { error = "search_term_too_long" });
        var size = Math.Clamp(limit ?? 20, 1, 50);
        var blocked = await db.Blocks.Where(b => b.PlayerId == http.PlayerId() || b.BlockedPlayerId == http.PlayerId())
            .Select(b => b.PlayerId == http.PlayerId() ? b.BlockedPlayerId : b.PlayerId).ToListAsync(ct);
        var query = db.Players.AsNoTracking().Where(p => p.Id != http.PlayerId() && !blocked.Contains(p.Id));
        if (term.Length > 0) query = query.Where(p => p.DisplayName.Contains(term) || p.UserName.Contains(term));
        var players = await query.OrderBy(p => p.UserName).Take(size).ToListAsync(ct);
        return Results.Ok(players.Select(SessionService.PublicPlayer));
    }

    private static async Task<IResult> ListFriendsAsync(HttpContext http, AppDbContext db, PresenceTracker presence, CancellationToken ct)
    {
        var id = http.PlayerId();
        var friendIds = await db.Friendships.Where(f => f.PlayerAId == id || f.PlayerBId == id)
            .Select(f => f.PlayerAId == id ? f.PlayerBId : f.PlayerAId).ToListAsync(ct);
        var friends = await db.Players.AsNoTracking().Where(p => friendIds.Contains(p.Id)).ToListAsync(ct);
        var incoming = await db.FriendRequests.Where(r => r.ToPlayerId == id && r.Status == "pending").ToListAsync(ct);
        var outgoing = await db.FriendRequests.Where(r => r.FromPlayerId == id && r.Status == "pending").ToListAsync(ct);
        return Results.Ok(new
        {
            friends = friends.Select(p => new { id = p.Id, userName = p.UserName, displayName = p.DisplayName, online = presence.IsOnline(p.Id), lastSeenAt = p.LastSeenAt }),
            incomingRequests = incoming,
            outgoingRequests = outgoing
        });
    }

    private static async Task<IResult> SendRequestAsync(FriendRequestBody request, HttpContext http, AppDbContext db, RealtimeEvents events, CancellationToken ct)
    {
        var from = http.PlayerId();
        if (request.PlayerId == Guid.Empty || request.PlayerId == from) return Results.BadRequest(new { error = "invalid_target" });
        if (!await db.Players.AnyAsync(p => p.Id == request.PlayerId, ct)) return Results.NotFound(new { error = "player_not_found" });
        if (await IsBlockedAsync(db, from, request.PlayerId, ct)) return Results.Conflict(new { error = "blocked" });
        if (await HasFriendshipAsync(db, from, request.PlayerId, ct)) return Results.Conflict(new { error = "already_friends" });
        var existing = await db.FriendRequests.FirstOrDefaultAsync(r => r.Status == "pending" &&
            ((r.FromPlayerId == from && r.ToPlayerId == request.PlayerId) || (r.FromPlayerId == request.PlayerId && r.ToPlayerId == from)), ct);
        if (existing is not null)
        {
            if (existing.FromPlayerId == request.PlayerId) return await RespondAsync(existing.Id, true, http, db, events, ct);
            return Results.Conflict(new { error = "request_already_pending", requestId = existing.Id });
        }
        var item = new FriendRequest { Id = Guid.NewGuid(), FromPlayerId = from, ToPlayerId = request.PlayerId, CreatedAt = DateTimeOffset.UtcNow };
        db.FriendRequests.Add(item);
        db.Notifications.Add(new Notification { Id = Guid.NewGuid(), PlayerId = request.PlayerId, Type = "friend.requested", PayloadJson = JsonSerializer.Serialize(new { requestId = item.Id, fromPlayerId = from }), CreatedAt = item.CreatedAt });
        await db.SaveChangesAsync(ct);
        await events.PlayerAsync(request.PlayerId, "friend.requested", new { requestId = item.Id, fromPlayerId = from }, ct);
        await events.PlayerAsync(request.PlayerId, "notification.created", new { type = "friend.requested", requestId = item.Id }, ct);
        return Results.Created($"/v1/friends/requests/{item.Id}", item);
    }

    private static async Task<IResult> RespondAsync(Guid requestId, bool accept, HttpContext http, AppDbContext db, RealtimeEvents events, CancellationToken ct)
    {
        var playerId = http.PlayerId();
        var request = await db.FriendRequests.FirstOrDefaultAsync(r => r.Id == requestId && r.ToPlayerId == playerId && r.Status == "pending", ct);
        if (request is null) return Results.NotFound();
        if (await IsBlockedAsync(db, request.FromPlayerId, request.ToPlayerId, ct)) return Results.Conflict(new { error = "blocked" });
        request.Status = accept ? "accepted" : "declined";
        request.RespondedAt = DateTimeOffset.UtcNow;
        if (accept)
        {
            var (a, b) = Ordered(request.FromPlayerId, request.ToPlayerId);
            if (!await db.Friendships.AnyAsync(f => f.PlayerAId == a && f.PlayerBId == b, ct))
                db.Friendships.Add(new Friendship { PlayerAId = a, PlayerBId = b, CreatedAt = DateTimeOffset.UtcNow });
        }
        db.Notifications.Add(new Notification { Id = Guid.NewGuid(), PlayerId = request.FromPlayerId, Type = accept ? "friend.accepted" : "friend.declined", PayloadJson = JsonSerializer.Serialize(new { playerId, requestId }), CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
        await events.PlayerAsync(request.FromPlayerId, "notification.created", new { type = accept ? "friend.accepted" : "friend.declined", playerId }, ct);
        return Results.Ok(new { requestId, status = request.Status, playerId = request.FromPlayerId });
    }

    private static async Task<IResult> RemoveFriendAsync(Guid playerId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var (a, b) = Ordered(http.PlayerId(), playerId);
        var friendship = await db.Friendships.FindAsync([a, b], ct);
        if (friendship is null) return Results.NotFound();
        db.Friendships.Remove(friendship);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> BlockAsync(Guid playerId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var actor = http.PlayerId();
        if (playerId == actor) return Results.BadRequest(new { error = "cannot_block_self" });
        if (!await db.Players.AnyAsync(p => p.Id == playerId, ct)) return Results.NotFound();
        if (!await db.Blocks.AnyAsync(b => b.PlayerId == actor && b.BlockedPlayerId == playerId, ct))
            db.Blocks.Add(new PlayerBlock { PlayerId = actor, BlockedPlayerId = playerId, CreatedAt = DateTimeOffset.UtcNow });
        var (a, b) = Ordered(actor, playerId);
        var friendship = await db.Friendships.FindAsync([a, b], ct);
        if (friendship is not null) db.Friendships.Remove(friendship);
        var requests = await db.FriendRequests.Where(r => r.Status == "pending" && ((r.FromPlayerId == actor && r.ToPlayerId == playerId) || (r.FromPlayerId == playerId && r.ToPlayerId == actor))).ToListAsync(ct);
        foreach (var request in requests) { request.Status = "declined"; request.RespondedAt = DateTimeOffset.UtcNow; }
        AuditLog.Add(db, actor, "player.block", "player", playerId.ToString());
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> UnblockAsync(Guid playerId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var block = await db.Blocks.FindAsync([http.PlayerId(), playerId], ct);
        if (block is null) return Results.NotFound();
        db.Blocks.Remove(block);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> MuteAsync(Guid playerId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var actor = http.PlayerId();
        if (playerId == actor) return Results.BadRequest(new { error = "cannot_mute_self" });
        if (!await db.Players.AnyAsync(p => p.Id == playerId, ct)) return Results.NotFound();
        if (!await db.Mutes.AnyAsync(m => m.PlayerId == actor && m.MutedPlayerId == playerId, ct))
            db.Mutes.Add(new PlayerMute { PlayerId = actor, MutedPlayerId = playerId, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> UnmuteAsync(Guid playerId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var mute = await db.Mutes.FindAsync([http.PlayerId(), playerId], ct);
        if (mute is null) return Results.NotFound();
        db.Mutes.Remove(mute);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetPresenceAsync(HttpContext http, AppDbContext db, PresenceTracker presence, CancellationToken ct)
    {
        var id = http.PlayerId();
        var friendIds = await db.Friendships.Where(f => f.PlayerAId == id || f.PlayerBId == id)
            .Select(f => f.PlayerAId == id ? f.PlayerBId : f.PlayerAId).ToListAsync(ct);
        var friends = await db.Players.AsNoTracking().Where(p => friendIds.Contains(p.Id)).ToListAsync(ct);
        return Results.Ok(friends.Select(p => new { playerId = p.Id, online = presence.IsOnline(p.Id), lastSeenAt = p.LastSeenAt }));
    }

    private static async Task<IResult> ListNotificationsAsync(int? limit, bool? unreadOnly, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 50, 1, 100);
        var query = db.Notifications.AsNoTracking().Where(n => n.PlayerId == http.PlayerId());
        if (unreadOnly == true) query = query.Where(n => n.ReadAt == null);
        var rows = await query.OrderByDescending(n => n.CreatedAt).Take(take).ToListAsync(ct);
        return Results.Ok(rows.Select(n => new { id = n.Id, type = n.Type, payload = JsonDocument.Parse(n.PayloadJson).RootElement.Clone(), createdAt = n.CreatedAt, readAt = n.ReadAt }));
    }

    private static async Task<IResult> MarkNotificationReadAsync(Guid notificationId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var notification = await db.Notifications.FirstOrDefaultAsync(n => n.Id == notificationId && n.PlayerId == http.PlayerId(), ct);
        if (notification is null) return Results.NotFound();
        notification.ReadAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { notification.Id, notification.ReadAt });
    }

    private static Task<bool> IsBlockedAsync(AppDbContext db, Guid a, Guid b, CancellationToken ct) =>
        db.Blocks.AnyAsync(x => (x.PlayerId == a && x.BlockedPlayerId == b) || (x.PlayerId == b && x.BlockedPlayerId == a), ct);

    private static Task<bool> HasFriendshipAsync(AppDbContext db, Guid a, Guid b, CancellationToken ct)
    {
        var (first, second) = Ordered(a, b);
        return db.Friendships.AnyAsync(x => x.PlayerAId == first && x.PlayerBId == second, ct);
    }

    public static (Guid First, Guid Second) Ordered(Guid a, Guid b) => a.CompareTo(b) < 0 ? (a, b) : (b, a);
}

public sealed record FriendRequestBody(Guid PlayerId);
