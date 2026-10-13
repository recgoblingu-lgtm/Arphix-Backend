# Revival Backend

An independent C#/.NET 8 backend for a new or authorized compatible social-room client. It owns its own accounts, identifiers, worlds, rooms, catalog, inventory, assets, persistence, and real-time events. **It does not call or depend on the original service's servers, APIs, authentication, assets, or live services.**

## Implemented scope

The repository now contains a durable API foundation with:

- SQLite and PostgreSQL persistence, provider-specific EF Core migrations, and a seeded starter catalog item.
- Password hashing, bearer sessions, refresh-token rotation/replay protection, logout, account lockout, profiles, settings, and progression.
- Friends, requests, blocks/mutes, online presence, notifications, parties, invites, and membership checks.
- Private/published worlds, discovery, favorites, reviews, room instances, capacity-limited joins, join tickets, region/mode matchmaking, and persistent queue assignment.
- Catalog, inventory, equipment, entitlements, rewards, and progression.
- Private conversations, message history/deletion, moderation reports, room voice-authorization sessions, and audit logging.
- Authorized project asset uploads, content hashes, immutable manifests, revocation, and file delivery.
- One-time admin bootstrap, player moderation, catalog management, metrics, and SQLite online backups.
- Authenticated SignalR/WebSocket hubs for player and room events.
- An end-to-end HTTP and SignalR smoke suite.

The exact route and event contract is in [`docs/API-CONTRACT.md`](docs/API-CONTRACT.md); module and deployment notes are in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

## Run locally

Install the .NET 8 SDK, then from the repository root:

```bash
dotnet restore src/Revival.Api/Revival.Api.csproj
dotnet run --project src/Revival.Api/Revival.Api.csproj
```

The default provider is SQLite. It creates `./data/revival.db`, applies migrations, and seeds `welcome-badge` on first start. Browse `http://localhost:5000/health` and `http://localhost:5000/v1` (or the listening URL printed by ASP.NET Core).

Override local paths and listen address as needed:

```bash
Database__SqlitePath=./data/local.db \
Storage__AssetRoot=./data/assets \
Storage__BackupRoot=./data/backups \
ASPNETCORE_URLS=http://0.0.0.0:5000 \
dotnet run --project src/Revival.Api/Revival.Api.csproj
```

## First administrator

Registration is open by default. Set a strong, private bootstrap secret **before starting the server**; the bootstrap endpoint is disabled when the secret is empty:

```bash
export Security__AdminBootstrapSecret="$(openssl rand -hex 32)"
dotnet run --project src/Revival.Api/Revival.Api.csproj
```

Register a normal account and use its returned `player.id` exactly once:

```bash
curl -X POST http://localhost:5000/v1/admin/bootstrap \
  -H "X-Admin-Bootstrap: $Security__AdminBootstrapSecret" \
  -H 'Content-Type: application/json' \
  -d '{"playerId":"<player-id-from-registration>"}'
```

Bootstrap succeeds only while no administrator exists. It is also possible to send the secret in an environment variable named `Security__AdminBootstrapSecret`; do not commit secrets or put them in source control.

## Verify the implementation

The full smoke suite starts an isolated temporary SQLite server, exercises persistence/auth/social/party/world/room/matchmaking/inventory/chat/admin/assets/backups, and opens a real SignalR WebSocket to verify event delivery. It requires the .NET 8 SDK and Node.js 22+:

```bash
dotnet build src/Revival.Api/Revival.Api.csproj
python3 tests/smoke.py
```

Generate or update migrations with the matching context and provider directory:

```bash
dotnet tool install --global dotnet-ef --version 8.0.20

dotnet ef migrations add <MigrationName> \
  --context SqliteAppDbContext \
  --project src/Revival.Api/Revival.Api.csproj \
  --startup-project src/Revival.Api/Revival.Api.csproj \
  --output-dir Data/Migrations/Sqlite

dotnet ef migrations add <MigrationName> \
  --context PostgresAppDbContext \
  --project src/Revival.Api/Revival.Api.csproj \
  --startup-project src/Revival.Api/Revival.Api.csproj \
  --output-dir Data/Migrations/Postgres
```

Always review generated migrations and test them against both database providers before deployment.

## Docker

The repository includes a multi-stage `Dockerfile` and persistent named data volume. For local use:

```bash
export REVIVAL_ADMIN_BOOTSTRAP_SECRET="$(openssl rand -hex 32)"
docker compose up --build
```

The API is exposed on port `8080`; its SQLite database, asset storage, and backups live in the `revival_data` volume. Set `REVIVAL_PORT` to change the host port. For PostgreSQL, set `REVIVAL_DATABASE_PROVIDER=Postgres` and `POSTGRES_CONNECTION_STRING` to a database you control. TLS should terminate at a trusted reverse proxy or load balancer.

## Configuration

| Setting | Purpose | Default |
|---|---|---|
| `Database:Provider` / `Database__Provider` | `Sqlite` or `Postgres` | `Sqlite` |
| `Database:SqlitePath` | SQLite database file | `./data/revival.db` |
| `ConnectionStrings:Postgres` or `POSTGRES_CONNECTION_STRING` | PostgreSQL connection | Not set |
| `Security:AdminBootstrapSecret` | One-time initial admin secret | Empty (bootstrap disabled) |
| `Storage:AssetRoot` | Authorized uploaded files | `./data/assets` |
| `Storage:BackupRoot` | SQLite backup output | `./data/backups` |
| `Cors:AllowedOrigins` | Explicit browser-client origins | Empty (CORS disabled) |
| `RateLimits:AuthPerMinute` | Auth route limit per client IP | `10` |
| `RateLimits:RequestsPerMinute` | General API limit per client IP | `300` |
| `Rooms:MembershipTimeoutSeconds` | Remove disconnected/stale room members after heartbeat timeout | `90` |
| `Rooms:MatchmakingTimeoutSeconds` | Expire unmatched requests | `180` |

See `src/Revival.Api/appsettings.json` for the complete defaults. The environment uses a project-owned user identity system; no original-service credentials or tokens are accepted.

## Important operational boundaries

This is a working backend implementation and integration surface, **not a complete hosted game platform**. The included voice endpoints authorize short-lived room sessions; they do not carry audio. Real-time room state is a validated host-proposed snapshot, not a server-side physics/game simulation. Asset provenance fields and size limits are enforced, but malware scanning and a public asset CDN are not included. The in-memory presence tracker and SignalR groups are single-process; multi-instance public deployment needs a distributed SignalR backplane/presence implementation. See the architecture document for the public-launch checklist.
