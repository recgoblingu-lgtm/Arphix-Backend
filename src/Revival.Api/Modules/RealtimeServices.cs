using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Revival.Api.Data;

namespace Revival.Api.Modules;

public sealed class PresenceTracker
{
    private readonly ConcurrentDictionary<Guid, int> _connections = new();
    public bool IsOnline(Guid playerId) => _connections.TryGetValue(playerId, out var count) && count > 0;
    public bool Connected(Guid playerId) => _connections.AddOrUpdate(playerId, 1, (_, old) => old + 1) == 1;
    public bool Disconnected(Guid playerId)
    {
        while (_connections.TryGetValue(playerId, out var old))
        {
            if (old <= 1) return ((ICollection<KeyValuePair<Guid, int>>)_connections).Remove(new KeyValuePair<Guid, int>(playerId, old));
            if (_connections.TryUpdate(playerId, old - 1, old)) return false;
        }
        return false;
    }
    public IReadOnlyCollection<Guid> OnlinePlayerIds => _connections.Where(x => x.Value > 0).Select(x => x.Key).ToArray();
}

public sealed class RealtimeEvents(IHubContext<RealtimeHub> hub)
{
    public Task PlayerAsync(Guid playerId, string eventName, object payload, CancellationToken ct = default) =>
        hub.Clients.Group(RealtimeHub.PlayerGroup(playerId)).SendAsync(eventName, payload, ct);

    public Task RoomAsync(Guid roomId, string eventName, object payload, CancellationToken ct = default) =>
        hub.Clients.Group(RealtimeHub.RoomGroup(roomId)).SendAsync(eventName, payload, ct);

    public Task BroadcastAsync(string eventName, object payload, CancellationToken ct = default) =>
        hub.Clients.All.SendAsync(eventName, payload, ct);
}

public sealed class RealtimeHub(AppDbContext db, SessionService sessions, PresenceTracker presence) : Hub
{
    public static string PlayerGroup(Guid id) => $"player:{id:N}";
    public static string RoomGroup(Guid id) => $"room:{id:N}";

    public async Task JoinRoom(Guid roomId)
    {
        if (!Context.Items.TryGetValue(AuthContextKeys.Player, out var value) || value is not Player player)
            throw new HubException("Unauthorized.");
        if (!await db.RoomMembers.AnyAsync(m => m.RoomId == roomId && m.PlayerId == player.Id, Context.ConnectionAborted))
            throw new HubException("Room membership is required.");
        await Groups.AddToGroupAsync(Context.ConnectionId, RoomGroup(roomId), Context.ConnectionAborted);
        var room = await db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roomId, Context.ConnectionAborted);
        if (room is not null) await Clients.Caller.SendAsync("room.state_changed", new { roomId, status = room.Status, sequence = room.StateSequence, state = System.Text.Json.JsonDocument.Parse(room.StateJson).RootElement.Clone() }, Context.ConnectionAborted);
    }

    public Task LeaveRoom(Guid roomId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, RoomGroup(roomId), Context.ConnectionAborted);

    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        if (http is null) { Context.Abort(); return; }
        if (!http.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && http.Request.Query.TryGetValue("access_token", out var queryToken) && !string.IsNullOrWhiteSpace(queryToken))
            http.Request.Headers.Authorization = $"Bearer {queryToken}";
        var player = await sessions.AuthenticateAsync(http, Context.ConnectionAborted);
        if (player is null) { Context.Abort(); return; }

        Context.Items[AuthContextKeys.Player] = player;
        await Groups.AddToGroupAsync(Context.ConnectionId, PlayerGroup(player.Id), Context.ConnectionAborted);
        foreach (var roomId in await db.RoomMembers.Where(m => m.PlayerId == player.Id)
                     .Select(m => m.RoomId).ToListAsync(Context.ConnectionAborted))
            await Groups.AddToGroupAsync(Context.ConnectionId, RoomGroup(roomId), Context.ConnectionAborted);

        var becameOnline = presence.Connected(player.Id);
        player.LastSeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(Context.ConnectionAborted);
        await Clients.Caller.SendAsync("session.ready", new
        {
            playerId = player.Id, protocolVersion = 1, serverVersion = "1.0.0",
            features = new[] { "presence", "parties", "rooms", "inventory", "chat", "notifications" }
        }, Context.ConnectionAborted);
        if (becameOnline) await PublishPresenceToFriendsAsync(player.Id, true);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(AuthContextKeys.Player, out var value) && value is Player player)
        {
            var becameOffline = presence.Disconnected(player.Id);
            player.LastSeenAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            if (becameOffline) await PublishPresenceToFriendsAsync(player.Id, false);
        }
        await base.OnDisconnectedAsync(exception);
    }

    // Room state is client-proposed but host-authorized, sequence-checked, and size-limited.
    public async Task PublishRoomState(Guid roomId, long sequence, JsonElement state)
    {
        if (!Context.Items.TryGetValue(AuthContextKeys.Player, out var value) || value is not Player player)
            throw new HubException("Unauthorized.");
        if (state.GetRawText().Length > 65_536) throw new HubException("Room state exceeds 64 KiB.");
        var member = await db.RoomMembers.AnyAsync(m => m.RoomId == roomId && m.PlayerId == player.Id, Context.ConnectionAborted);
        if (!member) throw new HubException("Room membership is required.");
        var room = await db.Rooms.FirstOrDefaultAsync(r => r.Id == roomId && r.Status == "open", Context.ConnectionAborted);
        if (room is null || room.HostId != player.Id) throw new HubException("Only the room host may publish room state.");
        if (sequence <= room.StateSequence) throw new HubException("Room state sequence must increase.");
        room.StateSequence = sequence;
        room.StateJson = state.GetRawText();
        room.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(Context.ConnectionAborted);
        await Clients.Group(RoomGroup(roomId)).SendAsync("room.state_changed", new { roomId, sequence, state, serverTime = room.UpdatedAt }, Context.ConnectionAborted);
    }

    private async Task PublishPresenceToFriendsAsync(Guid playerId, bool online)
    {
        var friendIds = await db.Friendships.Where(f => f.PlayerAId == playerId || f.PlayerBId == playerId)
            .Select(f => f.PlayerAId == playerId ? f.PlayerBId : f.PlayerAId).ToListAsync();
        foreach (var friendId in friendIds)
            await Clients.Group(PlayerGroup(friendId)).SendAsync("presence.changed", new { playerId, online, changedAt = DateTimeOffset.UtcNow });
    }
}

public static class AuditLog
{
    public static void Add(AppDbContext db, Guid? actor, string action, string targetType, string targetId, object? details = null)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(), ActorId = actor, Action = action, TargetType = targetType, TargetId = targetId,
            DetailsJson = JsonSerializer.Serialize(details ?? new { }), CreatedAt = DateTimeOffset.UtcNow
        });
    }
}

public sealed class SessionCleanupService(IServiceScopeFactory scopeFactory, ILogger<SessionCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
                await db.AuthSessions.Where(s => s.RefreshExpiresAt < cutoff || (s.RevokedAt != null && s.RevokedAt < cutoff)).ExecuteDeleteAsync(stoppingToken);
                await db.LoginAttempts.Where(a => a.WindowStartedAt < cutoff && (a.LockedUntil == null || a.LockedUntil < DateTimeOffset.UtcNow)).ExecuteDeleteAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Expired session cleanup failed.");
            }
        }
    }
}
