using System.Data;
using Microsoft.EntityFrameworkCore;
using Revival.Api.Data;

namespace Revival.Api.Modules;

public static class RoomModule
{
    public static void MapRoomEndpoints(this WebApplication app)
    {
        var rooms = app.MapGroup("/v1/rooms").AddEndpointFilter<PlayerAuthFilter>();
        rooms.MapPost("/", CreateAsync);
        rooms.MapGet("/{roomId:guid}", GetAsync);
        rooms.MapPost("/{roomId:guid}/join", JoinAsync);
        rooms.MapPost("/{roomId:guid}/leave", LeaveAsync);
        rooms.MapGet("/{roomId:guid}/players", ListPlayersAsync);
        rooms.MapPost("/{roomId:guid}/heartbeat", HeartbeatAsync);

        var queue = app.MapGroup("/v1/matchmaking").AddEndpointFilter<PlayerAuthFilter>();
        queue.MapPost("/requests", MatchmakeAsync);
        queue.MapGet("/requests/{requestId:guid}", GetMatchmakingAsync);
        queue.MapDelete("/requests/{requestId:guid}", CancelMatchmakingAsync);
    }

    private static async Task<IResult> CreateAsync(CreateRoomRequest request, HttpContext http, AppDbContext db, RealtimeEvents events, CancellationToken ct)
    {
        var world = await db.Worlds.FirstOrDefaultAsync(w => w.Id == request.WorldId && w.IsPublished, ct);
        if (world is null) return Results.NotFound(new { error = "published_world_not_found" });
        var playerId = http.PlayerId();
        var region = NormalizeMatchField(request.Region, "local");
        var mode = NormalizeMatchField(request.Mode, "default");
        if (region is null || mode is null) return Results.BadRequest(new { error = "invalid_room_region_or_mode" });
        var room = new Room { Id = Guid.NewGuid(), WorldId = world.Id, HostId = playerId, Capacity = Math.Clamp(request.Capacity ?? world.MaxPlayers, 2, world.MaxPlayers),
            Region = region, Mode = mode, Status = "open", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.Rooms.Add(room);
        db.RoomMembers.Add(new RoomMember { RoomId = room.Id, PlayerId = playerId, JoinedAt = room.CreatedAt, LastHeartbeatAt = room.CreatedAt });
        await db.SaveChangesAsync(ct);
        await MatchmakingAllocator.AssignWaitingAsync(room.Id, db, events, ct);
        await events.PlayerAsync(playerId, "room.state_changed", new { roomId = room.Id, status = room.Status, sequence = room.StateSequence }, ct);
        return Results.Created($"/v1/rooms/{room.Id}", new { room, joinTicket = await CreateJoinTicketAsync(room.Id, playerId, db, ct) });
    }

    private static async Task<IResult> GetAsync(Guid roomId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var room = await db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roomId, ct);
        if (room is null) return Results.NotFound();
        var world = await db.Worlds.AsNoTracking().FirstOrDefaultAsync(w => w.Id == room.WorldId, ct);
        if (world is null || (!world.IsPublished && room.HostId != http.PlayerId())) return Results.NotFound();
        var memberCount = await db.RoomMembers.CountAsync(m => m.RoomId == roomId, ct);
        return Results.Ok(new { room, memberCount, availableSlots = Math.Max(0, room.Capacity - memberCount) });
    }

    private static async Task<IResult> JoinAsync(Guid roomId, HttpContext http, AppDbContext db, RealtimeEvents events, CancellationToken ct)
    {
        var playerId = http.PlayerId();
        var room = await db.Rooms.FirstOrDefaultAsync(r => r.Id == roomId && r.Status == "open", ct);
        if (room is null) return Results.NotFound();
        var world = await db.Worlds.FirstOrDefaultAsync(w => w.Id == room.WorldId && w.IsPublished, ct);
        if (world is null) return Results.NotFound();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var existing = await db.RoomMembers.FindAsync([roomId, playerId], ct);
        var joinedNow = existing is null;
        if (existing is null)
        {
            var memberIds = await db.RoomMembers.Where(m => m.RoomId == roomId).Select(m => m.PlayerId).ToListAsync(ct);
            if (memberIds.Count >= room.Capacity) return Results.Conflict(new { error = "room_full" });
            var blocked = await db.Blocks.AnyAsync(b => (b.PlayerId == playerId && memberIds.Contains(b.BlockedPlayerId)) || (memberIds.Contains(b.PlayerId) && b.BlockedPlayerId == playerId), ct);
            if (blocked) return Results.Forbid();
            existing = new RoomMember { RoomId = roomId, PlayerId = playerId, JoinedAt = DateTimeOffset.UtcNow, LastHeartbeatAt = DateTimeOffset.UtcNow };
            db.RoomMembers.Add(existing);
        }
        else existing.LastHeartbeatAt = DateTimeOffset.UtcNow;
        var token = await CreateJoinTicketAsync(roomId, playerId, db, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        if (joinedNow)
            await events.RoomAsync(roomId, "room.player_joined", new { roomId, playerId, joinedAt = existing.JoinedAt }, ct);
        return Results.Ok(new { roomId, playerId, joinTicket = token, ticketExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2), stateSequence = room.StateSequence, state = room.StateJson });
    }

    private static async Task<IResult> LeaveAsync(Guid roomId, HttpContext http, AppDbContext db, RealtimeEvents events, CancellationToken ct)
    {
        var playerId = http.PlayerId();
        var room = await db.Rooms.FirstOrDefaultAsync(r => r.Id == roomId, ct);
        if (room is null) return Results.NotFound();
        var member = await db.RoomMembers.FindAsync([roomId, playerId], ct);
        if (member is null) return Results.NotFound();
        db.RoomMembers.Remove(member);
        if (room.HostId == playerId)
        {
            var successor = await db.RoomMembers.Where(m => m.RoomId == roomId).OrderBy(m => m.JoinedAt).FirstOrDefaultAsync(ct);
            if (successor is null) room.Status = "closed";
            else room.HostId = successor.PlayerId;
        }
        room.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await events.RoomAsync(roomId, "room.player_left", new { roomId, playerId, leftAt = room.UpdatedAt }, ct);
        if (room.Status == "closed") await events.RoomAsync(roomId, "room.state_changed", new { roomId, status = room.Status, sequence = room.StateSequence }, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ListPlayersAsync(Guid roomId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var room = await db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roomId && r.Status == "open", ct);
        if (room is null) return Results.NotFound();
        var world = await db.Worlds.AsNoTracking().FirstOrDefaultAsync(w => w.Id == room.WorldId, ct);
        if (world is null || (!world.IsPublished && room.HostId != http.PlayerId())) return Results.NotFound();
        var rows = await (from m in db.RoomMembers join p in db.Players on m.PlayerId equals p.Id
                          where m.RoomId == roomId orderby m.JoinedAt
                          select new { p.Id, p.UserName, p.DisplayName, m.JoinedAt, m.LastHeartbeatAt }).ToListAsync(ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> HeartbeatAsync(Guid roomId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var member = await db.RoomMembers.FindAsync([roomId, http.PlayerId()], ct);
        if (member is null) return Results.NotFound();
        member.LastHeartbeatAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { roomId, serverTime = member.LastHeartbeatAt });
    }

    private static async Task<IResult> MatchmakeAsync(MatchmakingRequestBody request, HttpContext http, AppDbContext db, RealtimeEvents events, CancellationToken ct)
    {
        var playerId = http.PlayerId();
        if (request.WorldId is not null && !await db.Worlds.AnyAsync(w => w.Id == request.WorldId && w.IsPublished, ct)) return Results.NotFound(new { error = "published_world_not_found" });
        var region = string.IsNullOrWhiteSpace(request.Region) ? null : NormalizeMatchField(request.Region, "local");
        var mode = string.IsNullOrWhiteSpace(request.Mode) ? null : NormalizeMatchField(request.Mode, "default");
        if ((!string.IsNullOrWhiteSpace(request.Region) && region is null) || (!string.IsNullOrWhiteSpace(request.Mode) && mode is null))
            return Results.BadRequest(new { error = "invalid_matchmaking_region_or_mode" });
        var active = await db.MatchmakingRequests.FirstOrDefaultAsync(r => r.PlayerId == playerId && r.Status == "searching", ct);
        if (active is not null) return Results.Ok(new { active.Id, active.WorldId, active.Status, active.RoomId, active.CreatedAt, active.CompletedAt });
        var item = new MatchmakingRequest { Id = Guid.NewGuid(), PlayerId = playerId, WorldId = request.WorldId, Region = region, Mode = mode, Status = "searching", CreatedAt = DateTimeOffset.UtcNow };
        var candidates = await db.Rooms.Where(r => r.Status == "open" && (request.WorldId == null || r.WorldId == request.WorldId)
                && (region == null || r.Region == region) && (mode == null || r.Mode == mode))
            .OrderBy(r => r.CreatedAt).Take(100).ToListAsync(ct);
        foreach (var room in candidates)
        {
            var memberIds = await db.RoomMembers.Where(m => m.RoomId == room.Id).Select(m => m.PlayerId).ToListAsync(ct);
            var alreadyMember = memberIds.Contains(playerId);
            if (!alreadyMember && memberIds.Count >= room.Capacity) continue;
            if (await db.Blocks.AnyAsync(b => (b.PlayerId == playerId && memberIds.Contains(b.BlockedPlayerId)) || (memberIds.Contains(b.PlayerId) && b.BlockedPlayerId == playerId), ct)) continue;
            if (!alreadyMember) db.RoomMembers.Add(new RoomMember { RoomId = room.Id, PlayerId = playerId, JoinedAt = DateTimeOffset.UtcNow, LastHeartbeatAt = DateTimeOffset.UtcNow });
            room.UpdatedAt = DateTimeOffset.UtcNow;
            item.Status = "matched"; item.RoomId = room.Id; item.CompletedAt = DateTimeOffset.UtcNow;
            break;
        }
        db.MatchmakingRequests.Add(item);
        await db.SaveChangesAsync(ct);
        var response = new { item.Id, item.WorldId, item.Status, item.RoomId, item.CreatedAt, item.CompletedAt };
        if (item.Status == "matched" && item.RoomId is not null)
        {
            await events.RoomAsync(item.RoomId.Value, "room.player_joined", new { roomId = item.RoomId, playerId, joinedAt = item.CompletedAt }, ct);
            await events.PlayerAsync(playerId, "matchmaking.matched", new { requestId = item.Id, roomId = item.RoomId, status = item.Status }, ct);
        }
        return item.Status == "matched" ? Results.Ok(response) : Results.Accepted($"/v1/matchmaking/requests/{item.Id}", response);
    }

    private static async Task<IResult> GetMatchmakingAsync(Guid requestId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var item = await db.MatchmakingRequests.FirstOrDefaultAsync(r => r.Id == requestId && r.PlayerId == http.PlayerId(), ct);
        return item is null ? Results.NotFound() : Results.Ok(new { item.Id, item.WorldId, item.Status, item.RoomId, item.CreatedAt, item.CompletedAt });
    }

    private static async Task<IResult> CancelMatchmakingAsync(Guid requestId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var item = await db.MatchmakingRequests.FirstOrDefaultAsync(r => r.Id == requestId && r.PlayerId == http.PlayerId(), ct);
        if (item is null) return Results.NotFound();
        if (item.Status == "matched") return Results.Conflict(new { error = "already_matched", roomId = item.RoomId });
        item.Status = "cancelled"; item.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<string> CreateJoinTicketAsync(Guid roomId, Guid playerId, AppDbContext db, CancellationToken ct)
    {
        var token = SessionService.NewToken();
        db.RoomJoinTickets.Add(new RoomJoinTicket { Id = Guid.NewGuid(), RoomId = roomId, PlayerId = playerId, TicketHash = SessionService.HashToken(token), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2) });
        await db.SaveChangesAsync(ct);
        return token;
    }

    private static string? NormalizeMatchField(string? value, string fallback)
    {
        var normalized = (string.IsNullOrWhiteSpace(value) ? fallback : value).Trim().ToLowerInvariant();
        return normalized.Length <= 48 && System.Text.RegularExpressions.Regex.IsMatch(normalized, "^[a-z0-9_.-]+$") ? normalized : null;
    }
}

public sealed record CreateRoomRequest(Guid WorldId, int? Capacity, string? Region = null, string? Mode = null);
public sealed record MatchmakingRequestBody(Guid? WorldId, string? Region = null, string? Mode = null);

public static class MatchmakingAllocator
{
    public static async Task AssignWaitingAsync(Guid roomId, AppDbContext db, RealtimeEvents events, CancellationToken ct)
    {
        var room = await db.Rooms.FirstOrDefaultAsync(r => r.Id == roomId && r.Status == "open", ct);
        if (room is null) return;
        var requests = await db.MatchmakingRequests.Where(r => r.Status == "searching"
                && (r.WorldId == null || r.WorldId == room.WorldId)
                && (r.Region == null || r.Region == room.Region)
                && (r.Mode == null || r.Mode == room.Mode))
            .OrderBy(r => r.CreatedAt).Take(100).ToListAsync(ct);
        var matches = new List<(Guid PlayerId, Guid RequestId)>();
        foreach (var request in requests)
        {
            var memberIds = await db.RoomMembers.Where(m => m.RoomId == roomId).Select(m => m.PlayerId).ToListAsync(ct);
            var alreadyMember = memberIds.Contains(request.PlayerId);
            if (!alreadyMember && memberIds.Count >= room.Capacity) break;
            if (await db.Blocks.AnyAsync(b => (b.PlayerId == request.PlayerId && memberIds.Contains(b.BlockedPlayerId)) || (memberIds.Contains(b.PlayerId) && b.BlockedPlayerId == request.PlayerId), ct)) continue;
            if (!alreadyMember)
                db.RoomMembers.Add(new RoomMember { RoomId = roomId, PlayerId = request.PlayerId, JoinedAt = DateTimeOffset.UtcNow, LastHeartbeatAt = DateTimeOffset.UtcNow });
            request.Status = "matched";
            request.RoomId = roomId;
            request.CompletedAt = DateTimeOffset.UtcNow;
            db.Notifications.Add(new Notification { Id = Guid.NewGuid(), PlayerId = request.PlayerId, Type = "matchmaking.matched",
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { requestId = request.Id, roomId }), CreatedAt = request.CompletedAt.Value });
            matches.Add((request.PlayerId, request.Id));
        }
        if (matches.Count == 0) return;
        room.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        foreach (var match in matches)
        {
            await events.PlayerAsync(match.PlayerId, "matchmaking.matched", new { requestId = match.RequestId, roomId, status = "matched" }, ct);
            await events.PlayerAsync(match.PlayerId, "notification.created", new { type = "matchmaking.matched", requestId = match.RequestId, roomId }, ct);
            await events.RoomAsync(roomId, "room.player_joined", new { roomId, playerId = match.PlayerId, joinedAt = DateTimeOffset.UtcNow }, ct);
        }
    }
}
