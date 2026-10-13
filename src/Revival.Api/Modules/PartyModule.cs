using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Revival.Api.Data;

namespace Revival.Api.Modules;

public static class PartyModule
{
    public static void MapPartyEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/v1/parties").AddEndpointFilter<PlayerAuthFilter>();
        group.MapPost("/", CreateAsync);
        group.MapGet("/{partyId:guid}", GetAsync);
        group.MapPost("/{partyId:guid}/members", AddMemberAsync);
        group.MapDelete("/{partyId:guid}/members/{playerId:guid}", RemoveMemberAsync);
        group.MapPost("/{partyId:guid}/invites", InviteAsync);
        group.MapPost("/{partyId:guid}/invites/{inviteId:guid}/accept", AcceptInviteAsync);
        group.MapPost("/{partyId:guid}/leave", LeaveAsync);
    }

    private static async Task<IResult> CreateAsync(CreatePartyRequest request, HttpContext http, AppDbContext db, RealtimeEvents events, CancellationToken ct)
    {
        var max = Math.Clamp(request.MaxMembers ?? 6, 2, 16);
        var party = new Party { Id = Guid.NewGuid(), OwnerId = http.PlayerId(), MaxMembers = max, CreatedAt = DateTimeOffset.UtcNow };
        db.Parties.Add(party);
        db.PartyMembers.Add(new PartyMember { PartyId = party.Id, PlayerId = party.OwnerId, JoinedAt = party.CreatedAt });
        await db.SaveChangesAsync(ct);
        await events.PlayerAsync(party.OwnerId, "party.updated", new { partyId = party.Id, action = "created", ownerId = party.OwnerId }, ct);
        return Results.Created($"/v1/parties/{party.Id}", party);
    }

    private static async Task<IResult> GetAsync(Guid partyId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var party = await db.Parties.AsNoTracking().FirstOrDefaultAsync(p => p.Id == partyId, ct);
        if (party is null) return Results.NotFound();
        var playerId = http.PlayerId();
        if (!await db.PartyMembers.AnyAsync(m => m.PartyId == partyId && m.PlayerId == playerId, ct)) return Results.Forbid();
        var members = await (from m in db.PartyMembers join p in db.Players on m.PlayerId equals p.Id
                             where m.PartyId == partyId orderby m.JoinedAt
                             select new { p.Id, p.UserName, p.DisplayName, m.JoinedAt, isOwner = p.Id == party.OwnerId }).ToListAsync(ct);
        var invites = party.OwnerId == playerId
            ? await db.PartyInvites.Where(i => i.PartyId == partyId && i.Status == "pending").ToListAsync(ct)
            : [];
        return Results.Ok(new { party, members, invites });
    }

    private static async Task<IResult> AddMemberAsync(Guid partyId, AddPartyMemberRequest request, HttpContext http, AppDbContext db, RealtimeEvents events, CancellationToken ct)
    {
        var actor = http.PlayerId();
        var party = await db.Parties.FirstOrDefaultAsync(p => p.Id == partyId, ct);
        if (party is null) return Results.NotFound();
        if (party.OwnerId != actor) return Results.Forbid();
        if (request.PlayerId == Guid.Empty || !await db.Players.AnyAsync(p => p.Id == request.PlayerId, ct)) return Results.NotFound(new { error = "player_not_found" });
        if (await db.Blocks.AnyAsync(b => (b.PlayerId == actor && b.BlockedPlayerId == request.PlayerId) || (b.PlayerId == request.PlayerId && b.BlockedPlayerId == actor), ct)) return Results.Conflict(new { error = "blocked" });
        if (await db.PartyMembers.AnyAsync(m => m.PartyId == partyId && m.PlayerId == request.PlayerId, ct)) return Results.Ok(new { partyId, playerId = request.PlayerId, status = "already_member" });
        var count = await db.PartyMembers.CountAsync(m => m.PartyId == partyId, ct);
        if (count >= party.MaxMembers) return Results.Conflict(new { error = "party_full" });
        db.PartyMembers.Add(new PartyMember { PartyId = partyId, PlayerId = request.PlayerId, JoinedAt = DateTimeOffset.UtcNow });
        var invite = await db.PartyInvites.FirstOrDefaultAsync(i => i.PartyId == partyId && i.InviteeId == request.PlayerId && i.Status == "pending", ct);
        if (invite is not null) invite.Status = "accepted";
        await db.SaveChangesAsync(ct);
        await PublishPartyAsync(db, events, partyId, "member_joined", request.PlayerId, ct);
        return Results.Ok(new { partyId, playerId = request.PlayerId, status = "joined" });
    }

    private static async Task<IResult> RemoveMemberAsync(Guid partyId, Guid playerId, HttpContext http, AppDbContext db, RealtimeEvents events, CancellationToken ct)
    {
        var actor = http.PlayerId();
        var party = await db.Parties.FirstOrDefaultAsync(p => p.Id == partyId, ct);
        if (party is null) return Results.NotFound();
        if (actor != playerId && party.OwnerId != actor) return Results.Forbid();
        return await RemoveMembershipAsync(party, playerId, db, events, ct);
    }

    private static async Task<IResult> LeaveAsync(Guid partyId, HttpContext http, AppDbContext db, RealtimeEvents events, CancellationToken ct)
    {
        var party = await db.Parties.FirstOrDefaultAsync(p => p.Id == partyId, ct);
        if (party is null) return Results.NotFound();
        return await RemoveMembershipAsync(party, http.PlayerId(), db, events, ct);
    }

    private static async Task<IResult> RemoveMembershipAsync(Party party, Guid playerId, AppDbContext db, RealtimeEvents events, CancellationToken ct)
    {
        var member = await db.PartyMembers.FindAsync([party.Id, playerId], ct);
        if (member is null) return Results.NotFound();
        db.PartyMembers.Remove(member);
        var oldOwner = party.OwnerId;
        if (oldOwner == playerId)
        {
            var next = await db.PartyMembers.Where(m => m.PartyId == party.Id).OrderBy(m => m.JoinedAt).FirstOrDefaultAsync(ct);
            if (next is null) db.Parties.Remove(party);
            else party.OwnerId = next.PlayerId;
        }
        await db.SaveChangesAsync(ct);
        if (await db.Parties.AnyAsync(p => p.Id == party.Id, ct)) await PublishPartyAsync(db, events, party.Id, "member_left", playerId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> InviteAsync(Guid partyId, PartyInviteRequest request, HttpContext http, AppDbContext db, RealtimeEvents events, CancellationToken ct)
    {
        var actor = http.PlayerId();
        var party = await db.Parties.FirstOrDefaultAsync(p => p.Id == partyId, ct);
        if (party is null) return Results.NotFound();
        if (party.OwnerId != actor) return Results.Forbid();
        if (request.PlayerId == actor || !await db.Players.AnyAsync(p => p.Id == request.PlayerId, ct)) return Results.BadRequest(new { error = "invalid_invitee" });
        if (await db.Blocks.AnyAsync(b => (b.PlayerId == actor && b.BlockedPlayerId == request.PlayerId) || (b.PlayerId == request.PlayerId && b.BlockedPlayerId == actor), ct)) return Results.Conflict(new { error = "blocked" });
        if (await db.PartyMembers.AnyAsync(m => m.PartyId == partyId && m.PlayerId == request.PlayerId, ct)) return Results.Conflict(new { error = "already_member" });
        var existing = await db.PartyInvites.FirstOrDefaultAsync(i => i.PartyId == partyId && i.InviteeId == request.PlayerId && i.Status == "pending", ct);
        if (existing is not null) return Results.Ok(existing);
        var invite = new PartyInvite { Id = Guid.NewGuid(), PartyId = partyId, InviterId = actor, InviteeId = request.PlayerId, CreatedAt = DateTimeOffset.UtcNow };
        db.PartyInvites.Add(invite);
        db.Notifications.Add(new Notification { Id = Guid.NewGuid(), PlayerId = request.PlayerId, Type = "party.invited", PayloadJson = JsonSerializer.Serialize(new { partyId, inviteId = invite.Id, inviterId = actor }), CreatedAt = invite.CreatedAt });
        await db.SaveChangesAsync(ct);
        await events.PlayerAsync(request.PlayerId, "notification.created", new { type = "party.invited", partyId, inviteId = invite.Id }, ct);
        return Results.Created($"/v1/parties/{partyId}/invites/{invite.Id}", invite);
    }

    private static async Task<IResult> AcceptInviteAsync(Guid partyId, Guid inviteId, HttpContext http, AppDbContext db, RealtimeEvents events, CancellationToken ct)
    {
        var playerId = http.PlayerId();
        var invite = await db.PartyInvites.FirstOrDefaultAsync(i => i.Id == inviteId && i.PartyId == partyId && i.InviteeId == playerId && i.Status == "pending", ct);
        if (invite is null) return Results.NotFound();
        var party = await db.Parties.FirstOrDefaultAsync(p => p.Id == partyId, ct);
        if (party is null) return Results.NotFound();
        var count = await db.PartyMembers.CountAsync(m => m.PartyId == partyId, ct);
        if (count >= party.MaxMembers) return Results.Conflict(new { error = "party_full" });
        invite.Status = "accepted";
        db.PartyMembers.Add(new PartyMember { PartyId = partyId, PlayerId = playerId, JoinedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
        await PublishPartyAsync(db, events, partyId, "member_joined", playerId, ct);
        return Results.Ok(new { partyId, playerId, status = "joined" });
    }

    private static async Task PublishPartyAsync(AppDbContext db, RealtimeEvents events, Guid partyId, string action, Guid playerId, CancellationToken ct)
    {
        var members = await db.PartyMembers.Where(m => m.PartyId == partyId).Select(m => m.PlayerId).ToListAsync(ct);
        var payload = new { partyId, action, playerId, changedAt = DateTimeOffset.UtcNow };
        foreach (var memberId in members) await events.PlayerAsync(memberId, "party.updated", payload, ct);
    }
}

public sealed record CreatePartyRequest(int? MaxMembers);
public sealed record AddPartyMemberRequest(Guid PlayerId);
public sealed record PartyInviteRequest(Guid PlayerId);
