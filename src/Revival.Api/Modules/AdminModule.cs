using System.Security.Cryptography;
using System.Text;
using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Revival.Api.Data;

namespace Revival.Api.Modules;

public static class AdminModule
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/admin/bootstrap", BootstrapAsync).RequireRateLimiting("auth");
        var admin = app.MapGroup("/v1/admin").AddEndpointFilter<PlayerAuthFilter>().AddEndpointFilter<AdminAuthFilter>();
        admin.MapGet("/health", HealthAsync);
        admin.MapGet("/players", ListPlayersAsync);
        admin.MapPost("/players/{playerId:guid}/suspend", SuspendAsync);
        admin.MapPost("/players/{playerId:guid}/unsuspend", UnsuspendAsync);
        admin.MapPost("/players/{playerId:guid}/inventory", GrantInventoryAsync);
        admin.MapGet("/reports", ListReportsAsync);
        admin.MapPost("/reports/{reportId:guid}/resolve", ResolveReportAsync);
        admin.MapGet("/audit-events", ListAuditEventsAsync);
        admin.MapPost("/catalog/items", CreateCatalogItemAsync);
        admin.MapPatch("/catalog/items/{itemId}", UpdateCatalogItemAsync);
        admin.MapPost("/backups", CreateBackupAsync);
        admin.MapGet("/metrics", MetricsAsync);
    }

    private static async Task<IResult> BootstrapAsync(BootstrapRequest request, HttpContext http, AppDbContext db, IConfiguration config, CancellationToken ct)
    {
        var expected = config["Security:AdminBootstrapSecret"];
        var supplied = http.Request.Headers["X-Admin-Bootstrap"].ToString();
        if (string.IsNullOrWhiteSpace(expected) || !FixedTimeEquals(expected, supplied)) return Results.NotFound();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (await db.Players.AnyAsync(p => p.IsAdmin, ct)) return Results.Conflict(new { error = "admin_already_bootstrapped" });
        var player = await db.Players.FirstOrDefaultAsync(p => p.Id == request.PlayerId, ct);
        if (player is null) return Results.NotFound(new { error = "player_not_found" });
        player.IsAdmin = true;
        AuditLog.Add(db, player.Id, "admin.bootstrap", "player", player.Id.ToString());
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { playerId = player.Id, isAdmin = true });
    }

    private static async Task<IResult> HealthAsync(AppDbContext db, CancellationToken ct)
    {
        var connected = await db.Database.CanConnectAsync(ct);
        return Results.Json(new { status = connected ? "ok" : "degraded", database = connected ? "connected" : "unavailable", utc = DateTimeOffset.UtcNow }, statusCode: connected ? 200 : 503);
    }

    private static async Task<IResult> ListPlayersAsync(string? q, int? limit, int? offset, AppDbContext db, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 50, 1, 200);
        var skip = Math.Clamp(offset ?? 0, 0, 100_000);
        var query = db.Players.AsNoTracking();
        var term = (q ?? "").Trim();
        if (term.Length > 0) query = query.Where(p => p.UserName.Contains(term) || p.DisplayName.Contains(term));
        var players = await query.OrderBy(p => p.CreatedAt).Skip(skip).Take(take).ToListAsync(ct);
        return Results.Ok(players.Select(p => new { p.Id, p.UserName, p.DisplayName, p.CreatedAt, p.IsAdmin, p.IsSuspended, p.SuspendedUntil, p.SuspensionReason, p.LastSeenAt }));
    }

    private static async Task<IResult> SuspendAsync(Guid playerId, SuspendPlayerRequest request, HttpContext http, AppDbContext db, RealtimeEvents events, CancellationToken ct)
    {
        if (playerId == http.PlayerId()) return Results.BadRequest(new { error = "cannot_suspend_self" });
        var player = await db.Players.FirstOrDefaultAsync(p => p.Id == playerId, ct);
        if (player is null) return Results.NotFound();
        if (player.IsAdmin) return Results.Conflict(new { error = "cannot_suspend_another_admin" });
        if (request.Until is not null && request.Until <= DateTimeOffset.UtcNow) return Results.BadRequest(new { error = "suspension_until_must_be_future" });
        if (request.Reason?.Length > 500) return Results.BadRequest(new { error = "reason_too_long" });
        player.IsSuspended = true;
        player.SuspendedUntil = request.Until;
        player.SuspensionReason = (request.Reason ?? "").Trim();
        var suspendedAt = DateTimeOffset.UtcNow;
        await db.AuthSessions.Where(s => s.PlayerId == playerId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, suspendedAt), ct);
        AuditLog.Add(db, http.PlayerId(), "player.suspend", "player", playerId.ToString(), new { player.SuspendedUntil, player.SuspensionReason });
        await db.SaveChangesAsync(ct);
        await events.PlayerAsync(playerId, "moderation.action", new { action = "suspended", until = player.SuspendedUntil, reason = player.SuspensionReason }, ct);
        return Results.Ok(new { playerId, suspended = true, player.SuspendedUntil, player.SuspensionReason });
    }

    private static async Task<IResult> UnsuspendAsync(Guid playerId, HttpContext http, AppDbContext db, RealtimeEvents events, CancellationToken ct)
    {
        var player = await db.Players.FirstOrDefaultAsync(p => p.Id == playerId, ct);
        if (player is null) return Results.NotFound();
        player.IsSuspended = false; player.SuspendedUntil = null; player.SuspensionReason = null;
        AuditLog.Add(db, http.PlayerId(), "player.unsuspend", "player", playerId.ToString());
        await db.SaveChangesAsync(ct);
        await events.PlayerAsync(playerId, "moderation.action", new { action = "unsuspended" }, ct);
        return Results.Ok(new { playerId, suspended = false });
    }

    private static async Task<IResult> GrantInventoryAsync(Guid playerId, GrantInventoryRequest request, HttpContext http, AppDbContext db, RealtimeEvents events, CancellationToken ct)
    {
        var player = await db.Players.FirstOrDefaultAsync(p => p.Id == playerId, ct);
        if (player is null) return Results.NotFound(new { error = "player_not_found" });
        var definitionId = (request.DefinitionId ?? "").Trim().ToLowerInvariant();
        var definition = await db.CatalogItems.FirstOrDefaultAsync(i => i.Id == definitionId && i.IsActive, ct);
        if (definition is null) return Results.NotFound(new { error = "active_catalog_item_not_found" });
        var quantity = request.Quantity ?? 1;
        if (quantity is < 1 or > 1_000_000) return Results.BadRequest(new { error = "quantity_out_of_range" });
        var existing = await db.Inventory.FirstOrDefaultAsync(i => i.PlayerId == playerId && i.DefinitionId == definitionId, ct);
        if (existing is null)
        {
            existing = new InventoryEntry { Id = Guid.NewGuid(), PlayerId = playerId, DefinitionId = definitionId, Quantity = quantity, GrantedAt = DateTimeOffset.UtcNow };
            db.Inventory.Add(existing);
        }
        else existing.Quantity = checked(existing.Quantity + quantity);
        if (!await db.Entitlements.AnyAsync(e => e.PlayerId == playerId && e.DefinitionId == definitionId && e.RevokedAt == null, ct))
            db.Entitlements.Add(new Entitlement { Id = Guid.NewGuid(), PlayerId = playerId, DefinitionId = definitionId, Source = (request.Source ?? "admin").Trim()[..Math.Min((request.Source ?? "admin").Trim().Length, 100)], GrantedAt = DateTimeOffset.UtcNow });
        AuditLog.Add(db, http.PlayerId(), "inventory.grant", "player", playerId.ToString(), new { definitionId, quantity });
        await db.SaveChangesAsync(ct);
        await events.PlayerAsync(playerId, "inventory.updated", new { playerId, inventoryId = existing.Id, definitionId, quantity = existing.Quantity }, ct);
        return Results.Ok(new { existing.Id, existing.PlayerId, existing.DefinitionId, existing.Quantity, existing.GrantedAt });
    }

    private static async Task<IResult> ListReportsAsync(string? status, int? limit, AppDbContext db, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 100, 1, 500);
        var query = db.Reports.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(r => r.Status == status.Trim().ToLowerInvariant());
        return Results.Ok(await query.OrderByDescending(r => r.CreatedAt).Take(take).ToListAsync(ct));
    }

    private static async Task<IResult> ResolveReportAsync(Guid reportId, ResolveReportRequest request, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var report = await db.Reports.FirstOrDefaultAsync(r => r.Id == reportId, ct);
        if (report is null) return Results.NotFound();
        if (report.Status != "open") return Results.Conflict(new { error = "report_already_resolved" });
        var resolution = (request.Resolution ?? "").Trim();
        if (resolution.Length is < 3 or > 1000) return Results.BadRequest(new { error = "resolution_required" });
        report.Status = "resolved"; report.Resolution = resolution; report.ResolvedById = http.PlayerId(); report.ResolvedAt = DateTimeOffset.UtcNow;
        AuditLog.Add(db, http.PlayerId(), "report.resolve", "report", reportId.ToString(), new { resolution });
        await db.SaveChangesAsync(ct);
        return Results.Ok(report);
    }

    private static async Task<IResult> ListAuditEventsAsync(int? limit, DateTimeOffset? before, AppDbContext db, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 100, 1, 500);
        var query = db.AuditEvents.AsNoTracking();
        if (before is not null) query = query.Where(e => e.CreatedAt < before.Value);
        return Results.Ok(await query.OrderByDescending(e => e.CreatedAt).Take(take).ToListAsync(ct));
    }

    private static async Task<IResult> CreateCatalogItemAsync(CatalogItemRequest request, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var id = (request.Id ?? "").Trim().ToLowerInvariant();
        var name = (request.Name ?? "").Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9][a-z0-9._-]{1,63}$") || name.Length is < 2 or > 100 || (request.Description?.Length ?? 0) > 1000)
            return Results.BadRequest(new { error = "invalid_catalog_item" });
        if (!TryNormalizeSlot(request.EquipmentSlot, out var slot)) return Results.BadRequest(new { error = "invalid_equipment_slot" });
        if (await db.CatalogItems.AnyAsync(i => i.Id == id, ct)) return Results.Conflict(new { error = "catalog_item_exists" });
        var item = new CatalogItem { Id = id, Name = name, Description = (request.Description ?? "").Trim(), EquipmentSlot = slot, CreatedAt = DateTimeOffset.UtcNow };
        db.CatalogItems.Add(item);
        AuditLog.Add(db, http.PlayerId(), "catalog.create", "catalogItem", id);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/v1/catalog/{item.Id}", item);
    }

    private static async Task<IResult> UpdateCatalogItemAsync(string itemId, CatalogItemPatch request, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var item = await db.CatalogItems.FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null) return Results.NotFound();
        if (request.Name is not null)
        {
            var value = request.Name.Trim();
            if (value.Length is < 2 or > 100) return Results.BadRequest(new { error = "invalid_item_name" });
            item.Name = value;
        }
        if (request.Description is not null)
        {
            if (request.Description.Length > 1000) return Results.BadRequest(new { error = "description_too_long" });
            item.Description = request.Description.Trim();
        }
        if (request.EquipmentSlot is not null)
        {
            if (!TryNormalizeSlot(request.EquipmentSlot, out var slot)) return Results.BadRequest(new { error = "invalid_equipment_slot" });
            item.EquipmentSlot = slot;
        }
        if (request.IsActive is not null) item.IsActive = request.IsActive.Value;
        AuditLog.Add(db, http.PlayerId(), "catalog.update", "catalogItem", itemId);
        await db.SaveChangesAsync(ct);
        return Results.Ok(item);
    }

    private static async Task<IResult> CreateBackupAsync(AppDbContext db, IConfiguration config, CancellationToken ct)
    {
        if (db.Database.ProviderName != "Microsoft.EntityFrameworkCore.Sqlite")
            return Results.Conflict(new { error = "sqlite_backup_endpoint_only", message = "For PostgreSQL, run pg_dump against the configured database and store the dump outside the web root." });
        var root = Path.GetFullPath(config["Storage:BackupRoot"] ?? "./data/backups");
        Directory.CreateDirectory(root);
        var name = $"revival-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db";
        var path = Path.Combine(root, name);
        var source = (SqliteConnection)db.Database.GetDbConnection();
        var closeSource = source.State != System.Data.ConnectionState.Open;
        if (closeSource) await source.OpenAsync(ct);
        try
        {
            await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
            await destination.OpenAsync(ct);
            source.BackupDatabase(destination);
            await destination.CloseAsync();
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch (PlatformNotSupportedException) { }
            }
        }
        finally { if (closeSource) await source.CloseAsync(); }
        return Results.Created($"/v1/admin/backups/{name}", new { fileName = name, createdAt = DateTimeOffset.UtcNow, sizeBytes = new FileInfo(path).Length });
    }

    private static async Task<IResult> MetricsAsync(AppDbContext db, CancellationToken ct) => Results.Ok(new
    {
        players = await db.Players.CountAsync(ct),
        activeSessions = await db.AuthSessions.CountAsync(s => s.RevokedAt == null && s.AccessExpiresAt > DateTimeOffset.UtcNow, ct),
        publishedWorlds = await db.Worlds.CountAsync(w => w.IsPublished, ct),
        openRooms = await db.Rooms.CountAsync(r => r.Status == "open", ct),
        queuedMatchmakingRequests = await db.MatchmakingRequests.CountAsync(r => r.Status == "searching", ct),
        openReports = await db.Reports.CountAsync(r => r.Status == "open", ct)
    });

    private static bool TryNormalizeSlot(string? slot, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(slot)) return true;
        var value = slot.Trim().ToLowerInvariant();
        if (value.Length > 32 || !System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-z0-9_-]+$")) return false;
        normalized = value;
        return true;
    }

    private static bool FixedTimeEquals(string expected, string supplied)
    {
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(supplied);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}

public sealed record BootstrapRequest(Guid PlayerId);
public sealed record SuspendPlayerRequest(DateTimeOffset? Until, string? Reason);
public sealed record ResolveReportRequest(string? Resolution);
public sealed record CatalogItemRequest(string? Id, string? Name, string? Description, string? EquipmentSlot);
public sealed record CatalogItemPatch(string? Name, string? Description, string? EquipmentSlot, bool? IsActive);
public sealed record GrantInventoryRequest(string? DefinitionId, int? Quantity, string? Source);
