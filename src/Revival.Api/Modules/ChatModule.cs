using Microsoft.EntityFrameworkCore;
using Revival.Api.Data;

namespace Revival.Api.Modules;

public static class ChatModule
{
    public static void MapChatEndpoints(this WebApplication app)
    {
        var chat = app.MapGroup("/v1/chat/conversations").AddEndpointFilter<PlayerAuthFilter>();
        chat.MapGet("/", ListConversationsAsync);
        chat.MapPost("/", CreateConversationAsync);
        chat.MapGet("/{conversationId:guid}/messages", ListMessagesAsync);
        chat.MapPost("/{conversationId:guid}/messages", SendMessageAsync);
        var messages = app.MapGroup("/v1/chat/messages").AddEndpointFilter<PlayerAuthFilter>();
        messages.MapDelete("/{messageId:guid}", DeleteMessageAsync);

        var voice = app.MapGroup("/v1/voice/rooms").AddEndpointFilter<PlayerAuthFilter>();
        voice.MapPost("/{roomId:guid}/join", JoinVoiceAsync);
        voice.MapPost("/{roomId:guid}/leave", LeaveVoiceAsync);

        var reports = app.MapGroup("/v1/reports").AddEndpointFilter<PlayerAuthFilter>();
        reports.MapPost("/", CreateReportAsync);
        reports.MapGet("/{reportId:guid}", GetReportAsync);
    }

    private static async Task<IResult> ListConversationsAsync(HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var playerId = http.PlayerId();
        var ids = await db.ConversationMembers.Where(m => m.PlayerId == playerId).Select(m => m.ConversationId).ToListAsync(ct);
        var rows = await db.Conversations.Where(c => ids.Contains(c.Id)).OrderByDescending(c => c.CreatedAt).Take(100).ToListAsync(ct);
        var result = new List<object>();
        foreach (var conversation in rows)
        {
            var participants = await (from m in db.ConversationMembers join p in db.Players on m.PlayerId equals p.Id
                                      where m.ConversationId == conversation.Id
                                      select new { p.Id, p.UserName, p.DisplayName }).ToListAsync(ct);
            var lastMessage = await db.ChatMessages.Where(m => m.ConversationId == conversation.Id && m.DeletedAt == null)
                .OrderByDescending(m => m.CreatedAt).Select(m => new { m.Id, m.SenderId, m.Text, m.CreatedAt }).FirstOrDefaultAsync(ct);
            result.Add(new { conversation.Id, conversation.CreatedById, conversation.CreatedAt, participants, lastMessage });
        }
        return Results.Ok(result);
    }

    private static async Task<IResult> CreateConversationAsync(CreateConversationRequest request, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var creator = http.PlayerId();
        var participantIds = (request.PlayerIds ?? []).Append(creator).Distinct().ToArray();
        if (participantIds.Length is < 2 or > 16) return Results.BadRequest(new { error = "conversation_requires_2_to_16_participants" });
        var existingIds = await db.Players.Where(p => participantIds.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct);
        if (existingIds.Count != participantIds.Length) return Results.BadRequest(new { error = "participant_not_found" });
        var others = participantIds.Where(p => p != creator).ToArray();
        if (await db.Blocks.AnyAsync(b => (b.PlayerId == creator && others.Contains(b.BlockedPlayerId)) || (others.Contains(b.PlayerId) && b.BlockedPlayerId == creator), ct))
            return Results.Forbid();
        var now = DateTimeOffset.UtcNow;
        var conversation = new Conversation { Id = Guid.NewGuid(), CreatedById = creator, CreatedAt = now };
        db.Conversations.Add(conversation);
        foreach (var id in participantIds) db.ConversationMembers.Add(new ConversationMember { ConversationId = conversation.Id, PlayerId = id, JoinedAt = now });
        await db.SaveChangesAsync(ct);
        return Results.Created($"/v1/chat/conversations/{conversation.Id}", new { conversation.Id, conversation.CreatedById, conversation.CreatedAt, participantIds });
    }

    private static async Task<IResult> ListMessagesAsync(Guid conversationId, int? limit, DateTimeOffset? before, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var playerId = http.PlayerId();
        if (!await db.ConversationMembers.AnyAsync(m => m.ConversationId == conversationId && m.PlayerId == playerId, ct)) return Results.NotFound();
        var take = Math.Clamp(limit ?? 50, 1, 100);
        var query = db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == conversationId);
        if (before is not null) query = query.Where(m => m.CreatedAt < before.Value);
        var rows = await query.OrderByDescending(m => m.CreatedAt).Take(take).ToListAsync(ct);
        rows.Reverse();
        return Results.Ok(rows.Select(m => new { m.Id, m.ConversationId, m.SenderId, text = m.DeletedAt is null ? m.Text : "[deleted]", m.CreatedAt, m.DeletedAt }));
    }

    private static async Task<IResult> SendMessageAsync(Guid conversationId, SendMessageRequest request, HttpContext http, AppDbContext db, RealtimeEvents events, CancellationToken ct)
    {
        var sender = http.PlayerId();
        var text = (request.Text ?? "").Trim();
        if (text.Length is < 1 or > 4000) return Results.BadRequest(new { error = "message_must_be_1_to_4000_characters" });
        if (!await db.ConversationMembers.AnyAsync(m => m.ConversationId == conversationId && m.PlayerId == sender, ct)) return Results.NotFound();
        var recipients = await db.ConversationMembers.Where(m => m.ConversationId == conversationId && m.PlayerId != sender).Select(m => m.PlayerId).ToListAsync(ct);
        if (await db.Blocks.AnyAsync(b => (b.PlayerId == sender && recipients.Contains(b.BlockedPlayerId)) || (recipients.Contains(b.PlayerId) && b.BlockedPlayerId == sender), ct))
            return Results.Conflict(new { error = "conversation_blocked" });
        var now = DateTimeOffset.UtcNow;
        var message = new ChatMessage { Id = Guid.NewGuid(), ConversationId = conversationId, SenderId = sender, Text = text, CreatedAt = now };
        db.ChatMessages.Add(message);
        await db.SaveChangesAsync(ct);
        var payload = new { message.Id, message.ConversationId, message.SenderId, message.Text, message.CreatedAt };
        foreach (var participantId in recipients)
        {
            if (await db.Mutes.AnyAsync(m => m.PlayerId == participantId && m.MutedPlayerId == sender, ct)) continue;
            await events.PlayerAsync(participantId, "chat.message", payload, ct);
        }
        return Results.Created($"/v1/chat/messages/{message.Id}", payload);
    }

    private static async Task<IResult> DeleteMessageAsync(Guid messageId, HttpContext http, AppDbContext db, RealtimeEvents events, CancellationToken ct)
    {
        var player = http.CurrentPlayer();
        var message = await db.ChatMessages.FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null) return Results.NotFound();
        if (message.SenderId != player.Id && !player.IsAdmin) return Results.Forbid();
        message.DeletedAt = DateTimeOffset.UtcNow;
        message.Text = "";
        await db.SaveChangesAsync(ct);
        var members = await db.ConversationMembers.Where(m => m.ConversationId == message.ConversationId).Select(m => m.PlayerId).ToListAsync(ct);
        foreach (var id in members) await events.PlayerAsync(id, "chat.message", new { messageId, message.ConversationId, deleted = true, deletedAt = message.DeletedAt }, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> JoinVoiceAsync(Guid roomId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var playerId = http.PlayerId();
        if (!await db.RoomMembers.AnyAsync(m => m.RoomId == roomId && m.PlayerId == playerId, ct)) return Results.Forbid();
        var room = await db.Rooms.FirstOrDefaultAsync(r => r.Id == roomId && r.Status == "open", ct);
        if (room is null) return Results.NotFound();
        var active = await db.VoiceSessions.Where(v => v.RoomId == roomId && v.PlayerId == playerId && v.EndedAt == null).ToListAsync(ct);
        foreach (var old in active) old.EndedAt = DateTimeOffset.UtcNow;
        var token = SessionService.NewToken();
        var session = new VoiceSession { Id = Guid.NewGuid(), RoomId = roomId, PlayerId = playerId, TokenHash = SessionService.HashToken(token), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) };
        db.VoiceSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { sessionId = session.Id, roomId, playerId, voiceToken = token, expiresAt = session.ExpiresAt, transport = "self-hosted-adapter-required" });
    }

    private static async Task<IResult> LeaveVoiceAsync(Guid roomId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var sessions = await db.VoiceSessions.Where(v => v.RoomId == roomId && v.PlayerId == http.PlayerId() && v.EndedAt == null).ToListAsync(ct);
        if (sessions.Count == 0) return Results.NotFound();
        foreach (var session in sessions) session.EndedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateReportAsync(CreateReportRequest request, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (request.TargetPlayerId is null && request.TargetWorldId is null && request.TargetMessageId is null)
            return Results.BadRequest(new { error = "report_target_required" });
        var category = (request.Category ?? "other").Trim().ToLowerInvariant();
        if (category.Length is < 2 or > 48 || (request.Description?.Length ?? 0) > 2000) return Results.BadRequest(new { error = "invalid_report_details" });
        if (request.TargetPlayerId is not null && !await db.Players.AnyAsync(p => p.Id == request.TargetPlayerId, ct)) return Results.NotFound(new { error = "target_player_not_found" });
        if (request.TargetWorldId is not null && !await db.Worlds.AnyAsync(w => w.Id == request.TargetWorldId, ct)) return Results.NotFound(new { error = "target_world_not_found" });
        if (request.TargetMessageId is not null && !await db.ChatMessages.AnyAsync(m => m.Id == request.TargetMessageId, ct)) return Results.NotFound(new { error = "target_message_not_found" });
        var report = new ModerationReport { Id = Guid.NewGuid(), ReporterId = http.PlayerId(), TargetPlayerId = request.TargetPlayerId,
            TargetWorldId = request.TargetWorldId, TargetMessageId = request.TargetMessageId, Category = category,
            Description = (request.Description ?? "").Trim(), CreatedAt = DateTimeOffset.UtcNow };
        db.Reports.Add(report);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/v1/reports/{report.Id}", new { report.Id, report.Status, report.CreatedAt });
    }

    private static async Task<IResult> GetReportAsync(Guid reportId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var report = await db.Reports.AsNoTracking().FirstOrDefaultAsync(r => r.Id == reportId, ct);
        if (report is null) return Results.NotFound();
        if (!http.CurrentPlayer().IsAdmin && report.ReporterId != http.PlayerId()) return Results.NotFound();
        return Results.Ok(report);
    }
}

public sealed record CreateConversationRequest(List<Guid>? PlayerIds);
public sealed record SendMessageRequest(string? Text);
public sealed record CreateReportRequest(Guid? TargetPlayerId, Guid? TargetWorldId, Guid? TargetMessageId, string? Category, string? Description);
