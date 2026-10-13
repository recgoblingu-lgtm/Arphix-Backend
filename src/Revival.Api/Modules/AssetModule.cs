using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Revival.Api.Data;

namespace Revival.Api.Modules;

public static class AssetModule
{
    private const long MaxUploadBytes = 50 * 1024 * 1024;

    public static void MapAssetEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/v1").AddEndpointFilter<PlayerAuthFilter>();
        group.MapPost("/worlds/{worldId:guid}/assets", UploadAsync)
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes + 1024 * 1024));
        group.MapGet("/assets/manifests/{manifestId:guid}", GetManifestAsync);
        group.MapGet("/assets/{assetId:guid}", DownloadAsync);
        group.MapDelete("/worlds/{worldId:guid}/assets/{assetId:guid}", RevokeAsync);
    }

    private static async Task<IResult> UploadAsync(Guid worldId, [FromForm] AssetUploadRequest request, HttpContext http, AppDbContext db, IConfiguration config, CancellationToken ct)
    {
        var playerId = http.PlayerId();
        var world = await db.Worlds.FirstOrDefaultAsync(w => w.Id == worldId, ct);
        if (world is null) return Results.NotFound();
        if (world.OwnerId != playerId && !http.CurrentPlayer().IsAdmin) return Results.Forbid();
        if (request.File is null || request.File.Length is <= 0 or > MaxUploadBytes) return Results.BadRequest(new { error = "asset_must_be_between_1_byte_and_50_mib" });
        var provenance = (request.Provenance ?? "").Trim();
        var authorization = (request.AuthorizationReference ?? "").Trim();
        if (provenance.Length is < 3 or > 500 || authorization.Length is < 3 or > 500)
            return Results.BadRequest(new { error = "provenance_and_authorization_reference_required" });

        var assetId = Guid.NewGuid();
        var root = Path.GetFullPath(config["Storage:AssetRoot"] ?? "./data/assets");
        Directory.CreateDirectory(root);
        var key = $"{assetId:N}.blob";
        var path = Path.Combine(root, key);
        string sha;
        try
        {
            await using var input = request.File.OpenReadStream();
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > MaxUploadBytes) { await output.DisposeAsync(); File.Delete(path); return Results.BadRequest(new { error = "asset_too_large" }); }
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            await output.FlushAsync(ct);
            sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }

        try
        {
            var version = (await db.AssetManifests.Where(m => m.WorldId == worldId).Select(m => (int?)m.Version).MaxAsync(ct) ?? 0) + 1;
            var currentAssets = await db.Assets.Where(a => a.WorldId == worldId && !a.IsRevoked).Select(a => a.Id).ToListAsync(ct);
            currentAssets.Add(assetId);
            var manifest = new AssetManifest { Id = Guid.NewGuid(), WorldId = worldId, Version = version, AssetIdsJson = JsonSerializer.Serialize(currentAssets), CreatedAt = DateTimeOffset.UtcNow };
            var safeName = Path.GetFileName((request.File.FileName ?? "asset.bin").Replace('\\', '/'));
            if (safeName.Length > 255) safeName = safeName[^255..];
            var asset = new WorldAsset
            {
                Id = assetId, WorldId = worldId, OwnerId = playerId, ManifestId = manifest.Id, OriginalName = safeName,
                StorageKey = key, ContentType = "application/octet-stream", SizeBytes = request.File.Length,
                Sha256 = sha, Provenance = provenance, AuthorizationReference = authorization, CreatedAt = DateTimeOffset.UtcNow
            };
            db.AssetManifests.Add(manifest);
            db.Assets.Add(asset);
            AuditLog.Add(db, playerId, "asset.upload", "asset", assetId.ToString(), new { worldId, sha256 = sha, sizeBytes = asset.SizeBytes, manifestVersion = version });
            await db.SaveChangesAsync(ct);
            return Results.Created($"/v1/assets/{asset.Id}", new { asset.Id, asset.WorldId, manifestId = manifest.Id, manifestVersion = version, asset.SizeBytes, sha256 = asset.Sha256, asset.CreatedAt });
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }

    private static async Task<IResult> GetManifestAsync(Guid manifestId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var manifest = await db.AssetManifests.AsNoTracking().FirstOrDefaultAsync(m => m.Id == manifestId, ct);
        if (manifest is null) return Results.NotFound();
        var world = await db.Worlds.AsNoTracking().FirstOrDefaultAsync(w => w.Id == manifest.WorldId, ct);
        if (world is null || (!world.IsPublished && world.OwnerId != http.PlayerId() && !http.CurrentPlayer().IsAdmin)) return Results.NotFound();
        return Results.Ok(new { manifest.Id, manifest.WorldId, manifest.Version, assets = JsonDocument.Parse(manifest.AssetIdsJson).RootElement.Clone(), manifest.CreatedAt });
    }

    private static async Task<IResult> DownloadAsync(Guid assetId, HttpContext http, AppDbContext db, IConfiguration config, CancellationToken ct)
    {
        var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assetId && !a.IsRevoked, ct);
        if (asset is null) return Results.NotFound();
        var world = await db.Worlds.AsNoTracking().FirstOrDefaultAsync(w => w.Id == asset.WorldId, ct);
        var player = http.CurrentPlayer();
        if (world is null || (!world.IsPublished && asset.OwnerId != player.Id && !player.IsAdmin)) return Results.NotFound();
        var root = Path.GetFullPath(config["Storage:AssetRoot"] ?? "./data/assets");
        var path = Path.GetFullPath(Path.Combine(root, asset.StorageKey));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path)) return Results.NotFound();
        http.Response.Headers.ETag = $"\"{asset.Sha256}\"";
        http.Response.Headers.CacheControl = world.IsPublished ? "public,max-age=3600,immutable" : "private,no-store";
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Results.File(stream, "application/octet-stream", enableRangeProcessing: true);
    }

    private static async Task<IResult> RevokeAsync(Guid worldId, Guid assetId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var world = await db.Worlds.FirstOrDefaultAsync(w => w.Id == worldId, ct);
        if (world is null) return Results.NotFound();
        if (world.OwnerId != http.PlayerId() && !http.CurrentPlayer().IsAdmin) return Results.Forbid();
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == assetId && a.WorldId == worldId, ct);
        if (asset is null) return Results.NotFound();
        if (asset.IsRevoked) return Results.NoContent();
        asset.IsRevoked = true;
        var version = (await db.AssetManifests.Where(m => m.WorldId == worldId).Select(m => (int?)m.Version).MaxAsync(ct) ?? 0) + 1;
        var remaining = await db.Assets.Where(a => a.WorldId == worldId && !a.IsRevoked && a.Id != assetId).Select(a => a.Id).ToListAsync(ct);
        var manifest = new AssetManifest { Id = Guid.NewGuid(), WorldId = worldId, Version = version, AssetIdsJson = JsonSerializer.Serialize(remaining), CreatedAt = DateTimeOffset.UtcNow };
        db.AssetManifests.Add(manifest);
        AuditLog.Add(db, http.PlayerId(), "asset.revoke", "asset", assetId.ToString(), new { worldId, manifestVersion = version });
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { assetId, revoked = true, manifestId = manifest.Id, manifestVersion = version });
    }
}

public sealed class AssetUploadRequest
{
    public IFormFile? File { get; set; }
    public string? Provenance { get; set; }
    public string? AuthorizationReference { get; set; }
}
