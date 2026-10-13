using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Revival.Api.Data;
using Revival.Api.Modules;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 52L * 1024 * 1024);
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 51L * 1024 * 1024);

var provider = builder.Configuration["Database:Provider"] ?? "Sqlite";
if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase) || provider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase))
{
    var connection = builder.Configuration.GetConnectionString("Postgres") ?? Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
    if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("Database:Provider=Postgres requires ConnectionStrings:Postgres or POSTGRES_CONNECTION_STRING.");
    builder.Services.AddDbContext<PostgresAppDbContext>(options => options.UseNpgsql(connection));
    builder.Services.AddScoped<AppDbContext>(services => services.GetRequiredService<PostgresAppDbContext>());
}
else if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase) || provider.Equals("SQLite", StringComparison.OrdinalIgnoreCase))
{
    var databasePath = builder.Configuration["Database:SqlitePath"] ?? Path.Combine(builder.Environment.ContentRootPath, "data", "revival.db");
    var fullDatabasePath = Path.GetFullPath(databasePath);
    Directory.CreateDirectory(Path.GetDirectoryName(fullDatabasePath)!);
    var connection = builder.Configuration.GetConnectionString("Sqlite") ?? $"Data Source={fullDatabasePath};Cache=Shared;Pooling=True;Default Timeout=30";
    builder.Services.AddDbContext<SqliteAppDbContext>(options => options.UseSqlite(connection));
    builder.Services.AddScoped<AppDbContext>(services => services.GetRequiredService<SqliteAppDbContext>());
}
else throw new InvalidOperationException("Database:Provider must be Sqlite or Postgres.");

builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped<PlayerAuthFilter>();
builder.Services.AddSingleton<RealtimeEvents>();
builder.Services.AddSingleton<PresenceTracker>();
builder.Services.AddHostedService<SessionCleanupService>();
builder.Services.AddHostedService<RoomLifecycleService>();
builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = 70 * 1024);
builder.Services.AddHealthChecks();
var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
if (origins.Length > 0)
{
    builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    {
        if (origins.Contains("*")) policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
        else policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
    }));
}
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, _) => await context.HttpContext.Response.WriteAsJsonAsync(new { error = "rate_limit_exceeded" });
    options.AddFixedWindowLimiter("auth", limiter =>
    {
        limiter.PermitLimit = builder.Configuration.GetValue("RateLimits:AuthPerMinute", 10);
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
    options.AddFixedWindowLimiter("realtime", limiter =>
    {
        limiter.PermitLimit = 30;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("RateLimits:RequestsPerMinute", 300),
            Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
        }));
});

var app = builder.Build();
app.UseExceptionHandler(error => error.Run(async context =>
{
    var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>();
    app.Logger.LogError(feature?.Error, "Unhandled request exception for {Path}", context.Request.Path);
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await context.Response.WriteAsJsonAsync(new { error = "internal_server_error", traceId = context.TraceIdentifier });
}));
if (origins.Length > 0) app.UseCors();
app.UseRateLimiter();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
    {
        await db.Database.OpenConnectionAsync();
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode=WAL;";
            await command.ExecuteScalarAsync();
        }
        await db.Database.CloseConnectionAsync();
    }
    await db.Database.MigrateAsync();
    if (!await db.CatalogItems.AnyAsync())
    {
        db.CatalogItems.Add(new CatalogItem { Id = "welcome-badge", Name = "Welcome Badge", Description = "A project-owned starter catalog item.", EquipmentSlot = "badge", IsActive = true, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }
}

app.MapGet("/health", async (AppDbContext db, CancellationToken ct) =>
{
    var connected = await db.Database.CanConnectAsync(ct);
    return Results.Json(new { status = connected ? "ok" : "degraded", service = "revival-api", database = connected ? "connected" : "unavailable", utc = DateTimeOffset.UtcNow }, statusCode: connected ? 200 : 503);
});
app.MapGet("/v1", () => Results.Ok(new
{
    protocolVersion = 1, serverVersion = "1.0.0", features = new[] { "identity", "profiles", "friends", "parties", "worlds", "rooms", "matchmaking", "inventory", "chat", "moderation", "assets", "realtime" },
    limits = new { maxUploadBytes = 50 * 1024 * 1024, maxChatMessageCharacters = 4000, maxRoomStateBytes = 65_536 }
}));
app.MapAuthEndpoints();
app.MapProfileEndpoints();
app.MapSocialEndpoints();
app.MapPartyEndpoints();
app.MapWorldEndpoints();
app.MapRoomEndpoints();
app.MapInventoryEndpoints();
app.MapChatEndpoints();
app.MapAssetEndpoints();
app.MapAdminEndpoints();
app.MapHub<RealtimeHub>("/v1/realtime").RequireRateLimiting("realtime");
app.Run();

public partial class Program { }
