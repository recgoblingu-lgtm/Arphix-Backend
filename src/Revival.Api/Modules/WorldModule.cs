using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Revival.Api.Data;

namespace Revival.Api.Modules;

public static class WorldModule
{
    public static void MapWorldEndpoints(this WebApplication app)
    {
        var worlds = app.MapGroup("/v1/worlds").AddEndpointFilter<PlayerAuthFilter>();
        worlds.MapGet("/", ListAsync);
        worlds.MapPost("/", CreateAsync);
        worlds.MapGet("/{worldId:guid}", GetAsync);
        worlds.MapPatch("/{worldId:guid}", UpdateAsync);
        worlds.MapDelete("/{worldId:guid}", DeleteAsync);
        worlds.MapPost("/{worldId:guid}/publish", (Guid worldId, HttpContext http, AppDbContext db, RealtimeEvents events, CancellationToken ct) => SetPublishedAsync(worldId, true, http, db, events, ct));
        worlds.MapPost("/{worldId:guid}/unpublish", (Guid worldId, HttpContext http, AppDbContext db, RealtimeEvents events, CancellationToken ct) => SetPublishedAsync(worldId, false, http, db, events, ct));
        worlds.MapPost("/{worldId:guid}/favorites", FavoriteAsync);
        worlds.MapGet("/{worldId:guid}/reviews", ListReviewsAsync);
        worlds.MapPost("/{worldId:guid}/reviews", SubmitReviewAsync);
    }

    private static async Task<IResult> ListAsync(string? q, string? tag, int? limit, int? offset, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var player = http.CurrentPlayer();
        var take = Math.Clamp(limit ?? 30, 1, 100);
        var skip = Math.Clamp(offset ?? 0, 0, 10_000);
        var query = db.Worlds.AsNoTracking().Where(w => w.IsPublished || w.OwnerId == player.Id || player.IsAdmin);
        var text = (q ?? "").Trim();
        if (text.Length > 0) query = query.Where(w => w.Name.Contains(text) || w.Description.Contains(text));
        var normalizedTag = (tag ?? "").Trim();
        if (normalizedTag.Length > 0) query = query.Where(w => w.TagsJson.Contains(normalizedTag));
        var worlds = await query.OrderByDescending(w => w.UpdatedAt).Skip(skip).Take(take).ToListAsync(ct);
        var ids = worlds.Select(w => w.Id).ToList();
        var counts = await db.WorldFavorites.Where(f => ids.Contains(f.WorldId)).GroupBy(f => f.WorldId).Select(g => new { WorldId = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.WorldId, x => x.Count, ct);
        var ratings = await db.WorldReviews.Where(r => ids.Contains(r.WorldId)).GroupBy(r => r.WorldId).Select(g => new { WorldId = g.Key, Count = g.Count(), Average = g.Average(r => r.Rating) }).ToDictionaryAsync(x => x.WorldId, ct);
        return Results.Ok(worlds.Select(w => new { world = w, favoriteCount = counts.GetValueOrDefault(w.Id), reviewCount = ratings.GetValueOrDefault(w.Id)?.Count ?? 0, averageRating = ratings.GetValueOrDefault(w.Id)?.Average ?? 0 }));
    }

    private static async Task<IResult> CreateAsync(CreateWorldRequest request, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var name = (request.Name ?? "").Trim();
        if (name.Length is < 2 or > 80) return Results.BadRequest(new { error = "invalid_world_name" });
        var description = (request.Description ?? "").Trim();
        if (description.Length > 2000) return Results.BadRequest(new { error = "description_too_long" });
        var tags = (request.Tags ?? []).Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0 && t.Length <= 32).Distinct().Take(12).ToArray();
        var playerId = http.PlayerId();
        var world = new World { Id = Guid.NewGuid(), OwnerId = playerId, Name = name, Description = description,
            TagsJson = JsonSerializer.Serialize(tags), MaxPlayers = Math.Clamp(request.MaxPlayers ?? 16, 2, 64),
            IsPublished = false, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.Worlds.Add(world);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/v1/worlds/{world.Id}", world);
    }

    private static async Task<IResult> GetAsync(Guid worldId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var world = await db.Worlds.AsNoTracking().FirstOrDefaultAsync(w => w.Id == worldId, ct);
        if (world is null) return Results.NotFound();
        if (!world.IsPublished && world.OwnerId != http.PlayerId() && !http.CurrentPlayer().IsAdmin) return Results.NotFound();
        var favorites = await db.WorldFavorites.CountAsync(f => f.WorldId == worldId, ct);
        var review = await db.WorldReviews.Where(r => r.WorldId == worldId).GroupBy(r => r.WorldId).Select(g => new { count = g.Count(), average = g.Average(x => x.Rating) }).FirstOrDefaultAsync(ct);
        var rooms = await db.Rooms.CountAsync(r => r.WorldId == worldId && r.Status == "open", ct);
        return Results.Ok(new { world, favoriteCount = favorites, reviewCount = review?.count ?? 0, averageRating = review?.average ?? 0, activeRooms = rooms });
    }

    private static async Task<IResult> UpdateAsync(Guid worldId, UpdateWorldRequest request, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var world = await db.Worlds.FirstOrDefaultAsync(w => w.Id == worldId, ct);
        if (world is null) return Results.NotFound();
        if (world.OwnerId != http.PlayerId() && !http.CurrentPlayer().IsAdmin) return Results.Forbid();
        if (request.Name is not null)
        {
            var value = request.Name.Trim();
            if (value.Length is < 2 or > 80) return Results.BadRequest(new { error = "invalid_world_name" });
            world.Name = value;
        }
        if (request.Description is not null)
        {
            if (request.Description.Length > 2000) return Results.BadRequest(new { error = "description_too_long" });
            world.Description = request.Description.Trim();
        }
        if (request.Tags is not null) world.TagsJson = JsonSerializer.Serialize(request.Tags.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length is > 0 and <= 32).Distinct().Take(12));
        if (request.MaxPlayers is not null) world.MaxPlayers = Math.Clamp(request.MaxPlayers.Value, 2, 64);
        world.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(world);
    }

    private static async Task<IResult> DeleteAsync(Guid worldId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var world = await db.Worlds.FirstOrDefaultAsync(w => w.Id == worldId, ct);
        if (world is null) return Results.NotFound();
        if (world.OwnerId != http.PlayerId() && !http.CurrentPlayer().IsAdmin) return Results.Forbid();
        if (await db.Rooms.AnyAsync(r => r.WorldId == worldId && r.Status == "open", ct)) return Results.Conflict(new { error = "world_has_active_rooms" });
        db.Worlds.Remove(world);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SetPublishedAsync(Guid worldId, bool publish, HttpContext http, AppDbContext db, RealtimeEvents events, CancellationToken ct)
    {
        var world = await db.Worlds.FirstOrDefaultAsync(w => w.Id == worldId, ct);
        if (world is null) return Results.NotFound();
        if (world.OwnerId != http.PlayerId() && !http.CurrentPlayer().IsAdmin) return Results.Forbid();
        world.IsPublished = publish;
        world.UpdatedAt = DateTimeOffset.UtcNow;
        AuditLog.Add(db, http.PlayerId(), publish ? "world.publish" : "world.unpublish", "world", world.Id.ToString());
        await db.SaveChangesAsync(ct);
        await events.PlayerAsync(world.OwnerId, "room.state_changed", new { worldId, published = publish, updatedAt = world.UpdatedAt }, ct);
        return Results.Ok(world);
    }

    private static async Task<IResult> FavoriteAsync(Guid worldId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (!await db.Worlds.AnyAsync(w => w.Id == worldId && w.IsPublished, ct)) return Results.NotFound();
        var playerId = http.PlayerId();
        var favorite = await db.WorldFavorites.FindAsync([worldId, playerId], ct);
        if (favorite is null) db.WorldFavorites.Add(new WorldFavorite { WorldId = worldId, PlayerId = playerId, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { worldId, favorited = true });
    }

    private static async Task<IResult> ListReviewsAsync(Guid worldId, int? limit, AppDbContext db, CancellationToken ct)
    {
        if (!await db.Worlds.AnyAsync(w => w.Id == worldId && w.IsPublished, ct)) return Results.NotFound();
        var rows = await (from r in db.WorldReviews join p in db.Players on r.PlayerId equals p.Id
                          where r.WorldId == worldId orderby r.UpdatedAt descending
                          select new { r.PlayerId, p.DisplayName, r.Rating, r.Text, r.CreatedAt, r.UpdatedAt })
            .Take(Math.Clamp(limit ?? 50, 1, 100)).ToListAsync(ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> SubmitReviewAsync(Guid worldId, ReviewRequest request, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (request.Rating is < 1 or > 5 || (request.Text?.Length ?? 0) > 1000) return Results.BadRequest(new { error = "rating_must_be_1_to_5_and_text_under_1000" });
        var world = await db.Worlds.FirstOrDefaultAsync(w => w.Id == worldId && w.IsPublished, ct);
        if (world is null) return Results.NotFound();
        var playerId = http.PlayerId();
        if (world.OwnerId == playerId) return Results.BadRequest(new { error = "cannot_review_own_world" });
        var review = await db.WorldReviews.FindAsync([worldId, playerId], ct);
        if (review is null)
        {
            review = new WorldReview { WorldId = worldId, PlayerId = playerId, CreatedAt = DateTimeOffset.UtcNow };
            db.WorldReviews.Add(review);
        }
        review.Rating = request.Rating;
        review.Text = (request.Text ?? "").Trim();
        review.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(review);
    }
}

public sealed record CreateWorldRequest(string? Name, string? Description, List<string>? Tags, int? MaxPlayers);
public sealed record UpdateWorldRequest(string? Name, string? Description, List<string>? Tags, int? MaxPlayers);
public sealed record ReviewRequest(int Rating, string? Text);
