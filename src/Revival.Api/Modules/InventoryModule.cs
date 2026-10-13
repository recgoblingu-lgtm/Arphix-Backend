using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Revival.Api.Data;

namespace Revival.Api.Modules;

public static class InventoryModule
{
    public static void MapInventoryEndpoints(this WebApplication app)
    {
        var catalog = app.MapGroup("/v1/catalog").AddEndpointFilter<PlayerAuthFilter>();
        catalog.MapGet("/", ListCatalogAsync);
        catalog.MapGet("/{itemId}", GetCatalogItemAsync);

        var inventory = app.MapGroup("/v1/inventory").AddEndpointFilter<PlayerAuthFilter>();
        inventory.MapGet("/", GetInventoryAsync);
        inventory.MapPost("/equip", EquipAsync);
        inventory.MapPost("/unequip", UnequipAsync);

        var entitlements = app.MapGroup("/v1/entitlements").AddEndpointFilter<PlayerAuthFilter>();
        entitlements.MapGet("/", GetEntitlementsAsync);
        var progression = app.MapGroup("/v1/progression").AddEndpointFilter<PlayerAuthFilter>();
        progression.MapGet("/", GetProgressionAsync);
        var rewards = app.MapGroup("/v1/rewards").AddEndpointFilter<PlayerAuthFilter>();
        rewards.MapPost("/claim", ClaimRewardAsync);
    }

    private static async Task<IResult> ListCatalogAsync(string? q, int? limit, AppDbContext db, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 100, 1, 200);
        var query = db.CatalogItems.AsNoTracking().Where(i => i.IsActive);
        var term = (q ?? "").Trim();
        if (term.Length > 0) query = query.Where(i => i.Name.Contains(term) || i.Description.Contains(term));
        return Results.Ok(await query.OrderBy(i => i.Name).Take(take).ToListAsync(ct));
    }

    private static async Task<IResult> GetCatalogItemAsync(string itemId, AppDbContext db, CancellationToken ct)
    {
        var item = await db.CatalogItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId && i.IsActive, ct);
        return item is null ? Results.NotFound() : Results.Ok(item);
    }

    private static async Task<IResult> GetInventoryAsync(HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var id = http.PlayerId();
        var rows = await (from entry in db.Inventory.AsNoTracking()
                          join definition in db.CatalogItems.AsNoTracking() on entry.DefinitionId equals definition.Id into defs
                          from definition in defs.DefaultIfEmpty()
                          where entry.PlayerId == id orderby entry.GrantedAt descending
                          select new { entry.Id, entry.DefinitionId, entry.Quantity, entry.IsEquipped, entry.GrantedAt,
                              definition = definition == null ? null : new { definition.Name, definition.Description, definition.EquipmentSlot } }).ToListAsync(ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> EquipAsync(EquipItemRequest request, HttpContext http, AppDbContext db, RealtimeEvents events, CancellationToken ct)
    {
        var id = http.PlayerId();
        var entry = await db.Inventory.FirstOrDefaultAsync(i => i.Id == request.InventoryId && i.PlayerId == id && i.Quantity > 0, ct);
        if (entry is null) return Results.NotFound(new { error = "inventory_item_not_found" });
        var definition = await db.CatalogItems.FirstOrDefaultAsync(i => i.Id == entry.DefinitionId && i.IsActive, ct);
        if (definition is null) return Results.Conflict(new { error = "item_definition_unavailable" });
        if (string.IsNullOrWhiteSpace(definition.EquipmentSlot)) return Results.BadRequest(new { error = "item_not_equipable" });
        var slotIds = await db.CatalogItems.Where(i => i.EquipmentSlot == definition.EquipmentSlot).Select(i => i.Id).ToListAsync(ct);
        var others = await db.Inventory.Where(i => i.PlayerId == id && i.IsEquipped && i.Id != entry.Id && slotIds.Contains(i.DefinitionId)).ToListAsync(ct);
        foreach (var other in others) other.IsEquipped = false;
        entry.IsEquipped = true;
        await db.SaveChangesAsync(ct);
        var payload = new { playerId = id, inventoryId = entry.Id, definitionId = entry.DefinitionId, slot = definition.EquipmentSlot, equipped = true };
        await events.PlayerAsync(id, "inventory.updated", payload, ct);
        return Results.Ok(payload);
    }

    private static async Task<IResult> UnequipAsync(EquipItemRequest request, HttpContext http, AppDbContext db, RealtimeEvents events, CancellationToken ct)
    {
        var id = http.PlayerId();
        var entry = await db.Inventory.FirstOrDefaultAsync(i => i.Id == request.InventoryId && i.PlayerId == id, ct);
        if (entry is null) return Results.NotFound();
        entry.IsEquipped = false;
        await db.SaveChangesAsync(ct);
        var payload = new { playerId = id, inventoryId = entry.Id, definitionId = entry.DefinitionId, equipped = false };
        await events.PlayerAsync(id, "inventory.updated", payload, ct);
        return Results.Ok(payload);
    }

    private static async Task<IResult> GetEntitlementsAsync(HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var rows = await db.Entitlements.AsNoTracking().Where(e => e.PlayerId == http.PlayerId() && e.RevokedAt == null).OrderByDescending(e => e.GrantedAt).ToListAsync(ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetProgressionAsync(HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var player = await db.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == http.PlayerId(), ct);
        return player is null ? Results.NotFound() : Results.Content(player.ProgressionJson, "application/json");
    }

    private static async Task<IResult> ClaimRewardAsync(ClaimRewardRequest request, HttpContext http, AppDbContext db, RealtimeEvents events, CancellationToken ct)
    {
        var rewardId = (request.RewardId ?? "").Trim().ToLowerInvariant();
        if (rewardId is not ("welcome" or "daily")) return Results.NotFound(new { error = "reward_not_found", available = new[] { "welcome", "daily" } });
        var player = await db.Players.FirstOrDefaultAsync(p => p.Id == http.PlayerId(), ct);
        if (player is null) return Results.Unauthorized();
        var claimKey = rewardId == "welcome" ? "welcome" : $"daily:{DateTimeOffset.UtcNow:yyyyMMdd}";
        if (await db.RewardClaims.AnyAsync(c => c.PlayerId == player.Id && c.ClaimKey == claimKey, ct)) return Results.Conflict(new { error = "reward_already_claimed" });
        var progression = JsonNode.Parse(player.ProgressionJson) as JsonObject ?? new JsonObject();
        var coins = progression["coins"]?.GetValue<int>() ?? 0;
        var experience = progression["experience"]?.GetValue<int>() ?? 0;
        var coinGrant = rewardId == "welcome" ? 100 : 25;
        var xpGrant = rewardId == "welcome" ? 100 : 10;
        progression["coins"] = coins + coinGrant;
        progression["experience"] = experience + xpGrant;
        progression["level"] = Math.Max(1, (experience + xpGrant) / 1000 + 1);
        player.ProgressionJson = progression.ToJsonString();
        var now = DateTimeOffset.UtcNow;
        db.RewardClaims.Add(new RewardClaim { Id = Guid.NewGuid(), PlayerId = player.Id, ClaimKey = claimKey, RewardJson = JsonSerializer.Serialize(new { rewardId, coins = coinGrant, experience = xpGrant }), ClaimedAt = now });
        if (rewardId == "welcome" && await db.CatalogItems.AnyAsync(i => i.Id == "welcome-badge" && i.IsActive, ct))
        {
            if (!await db.Inventory.AnyAsync(i => i.PlayerId == player.Id && i.DefinitionId == "welcome-badge", ct))
                db.Inventory.Add(new InventoryEntry { Id = Guid.NewGuid(), PlayerId = player.Id, DefinitionId = "welcome-badge", Quantity = 1, GrantedAt = now });
            if (!await db.Entitlements.AnyAsync(e => e.PlayerId == player.Id && e.DefinitionId == "welcome-badge" && e.RevokedAt == null, ct))
                db.Entitlements.Add(new Entitlement { Id = Guid.NewGuid(), PlayerId = player.Id, DefinitionId = "welcome-badge", Source = "welcome-reward", GrantedAt = now });
        }
        AuditLog.Add(db, player.Id, "reward.claim", "reward", rewardId, new { coins = coinGrant, experience = xpGrant });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return Results.Conflict(new { error = "reward_already_claimed" }); }
        var response = new { rewardId, coinsGranted = coinGrant, experienceGranted = xpGrant, progression = JsonDocument.Parse(player.ProgressionJson).RootElement.Clone(), claimedAt = now };
        await events.PlayerAsync(player.Id, "inventory.updated", new { playerId = player.Id, rewardId, claimedAt = now }, ct);
        return Results.Ok(response);
    }
}

public sealed record EquipItemRequest(Guid InventoryId);
public sealed record ClaimRewardRequest(string? RewardId);
