using Microsoft.EntityFrameworkCore;
using Revival.Api.Data;

namespace Revival.Api.Modules;

public sealed class RoomLifecycleService(
    IServiceScopeFactory scopeFactory,
    PresenceTracker presence,
    RealtimeEvents events,
    IConfiguration configuration,
    ILogger<RoomLifecycleService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = Math.Clamp(configuration.GetValue("Rooms:CleanupIntervalSeconds", 15), 1, 300);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await SweepAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Room and matchmaking lifecycle cleanup failed.");
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var memberTimeoutSeconds = Math.Clamp(configuration.GetValue("Rooms:MembershipTimeoutSeconds", 90), 15, 3600);
        var staleBefore = now.AddSeconds(-memberTimeoutSeconds);
        var matchmakingTimeoutSeconds = Math.Clamp(configuration.GetValue("Rooms:MatchmakingTimeoutSeconds", 180), 30, 3600);
        var queueBefore = now.AddSeconds(-matchmakingTimeoutSeconds);
        var roomEvents = new List<(Guid RoomId, Guid PlayerId, bool Closed, Guid HostId)>();

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var openRooms = await db.Rooms.Where(r => r.Status == "open").ToListAsync(ct);
        foreach (var room in openRooms)
        {
            var staleMembers = await db.RoomMembers.Where(m => m.RoomId == room.Id && m.LastHeartbeatAt < staleBefore).ToListAsync(ct);
            var removed = staleMembers.Where(m => !presence.IsOnline(m.PlayerId)).ToList();
            if (removed.Count == 0) continue;
            var removedPlayerIds = removed.Select(m => m.PlayerId).ToArray();
            foreach (var member in removed) db.RoomMembers.Remove(member);
            var remaining = await db.RoomMembers.Where(m => m.RoomId == room.Id && !removedPlayerIds.Contains(m.PlayerId))
                .OrderBy(m => m.JoinedAt).FirstOrDefaultAsync(ct);
            var hostRemoved = removedPlayerIds.Contains(room.HostId);
            var closed = false;
            if (remaining is null)
            {
                room.Status = "closed";
                closed = true;
            }
            else if (hostRemoved) room.HostId = remaining.PlayerId;
            room.UpdatedAt = now;
            foreach (var member in removed) roomEvents.Add((room.Id, member.PlayerId, closed, room.HostId));
        }

        var expiredRequests = await db.MatchmakingRequests.Where(r => r.Status == "searching" && r.CreatedAt < queueBefore).ToListAsync(ct);
        foreach (var request in expiredRequests)
        {
            request.Status = "expired";
            request.CompletedAt = now;
            db.Notifications.Add(new Notification
            {
                Id = Guid.NewGuid(), PlayerId = request.PlayerId, Type = "matchmaking.expired",
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { requestId = request.Id }), CreatedAt = now
            });
        }
        if (roomEvents.Count == 0 && expiredRequests.Count == 0) return;
        await db.SaveChangesAsync(ct);

        foreach (var entry in roomEvents)
        {
            await events.RoomAsync(entry.RoomId, "room.player_left", new { roomId = entry.RoomId, playerId = entry.PlayerId, leftAt = now }, ct);
            if (entry.Closed) await events.RoomAsync(entry.RoomId, "room.state_changed", new { roomId = entry.RoomId, status = "closed", hostId = entry.HostId }, ct);
            else await events.RoomAsync(entry.RoomId, "room.state_changed", new { roomId = entry.RoomId, status = "open", hostId = entry.HostId }, ct);
        }
        foreach (var request in expiredRequests)
        {
            await events.PlayerAsync(request.PlayerId, "matchmaking.expired", new { requestId = request.Id, status = request.Status }, ct);
            await events.PlayerAsync(request.PlayerId, "notification.created", new { type = "matchmaking.expired", requestId = request.Id }, ct);
        }
    }
}
