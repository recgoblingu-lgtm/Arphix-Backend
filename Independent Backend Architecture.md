# Independent Backend Architecture

This is a standalone backend for a new or authorized compatible client. It owns the application contract and does not call the original service's APIs or use its identity, database, assets, or real-time infrastructure.

## Runtime and persistence

- .NET 8 / ASP.NET Core minimal APIs.
- EF Core 8 with separate concrete contexts and migration histories for SQLite and PostgreSQL.
- SQLite is the default local/development database; writes use WAL mode and the online backup API.
- PostgreSQL is the production database option; the same EF model is compiled into provider-specific migrations.
- UTC `DateTimeOffset` values are stored as UTC ticks to support ordered expiry/window queries on SQLite and PostgreSQL.
- Uploaded files are stored under a configurable local directory; asset metadata, SHA-256, provenance, authorization reference, immutable manifest versions, and revocation state are persisted.
- Database schema migration runs before the server begins accepting traffic. The starter catalog seed is applied idempotently.

The initial migration sets are in `src/Revival.Api/Data/Migrations/Sqlite` and `src/Revival.Api/Data/Migrations/Postgres`. Every schema change must update both sets and be tested against both providers.

## Modules

- Authentication, password hashing, bearer/refresh sessions, logout/replay handling, and persistent account lockout.
- Player profiles, privacy settings, and progression.
- Friend requests, friendships, blocks, mutes, online presence, notifications, parties, and party invites.
- Worlds, publishing, discovery, favorites, reviews, room instances, capacity-limited membership, heartbeats, join tickets, and region/mode-aware matchmaking.
- Catalog, inventory, equipment slots, entitlements, starter/daily rewards, and progression.
- Private conversations, bounded/paginated messages, deletion, moderation reports, and short-lived voice authorization sessions.
- Authorized asset upload/download, content-addressed integrity metadata, immutable manifests, and revocation.
- One-time administrator bootstrap, suspension, report queue, catalog/inventory administration, audit log, metrics, health, and SQLite backups.
- Authenticated SignalR hub for player and room events.

Modules use the shared `AppDbContext` model and project-owned IDs. Authorization is checked in route handlers and endpoint filters; mutations add audit or notification records where applicable.

## Identity and real-time security

Passwords are hashed with PBKDF2-HMAC-SHA256 and a random salt. Access and refresh tokens are cryptographically random opaque values; only SHA-256 token hashes are stored. Refresh rotates the session token pair, and a detected refresh-token replay revokes active sessions. Suspended accounts cannot authenticate. Login failures are counted by normalized account name and result in a timed lock after repeated failures; request-level rate limits are also applied by source IP.

HTTP requests use `Authorization: Bearer <access-token>`. The SignalR negotiate request and hub connection use the same bearer token. Hubs add authenticated users to private player groups and authorize room group joins against persistent room membership. Clients must treat tokens as secrets and must use TLS outside localhost.

The initial administrator is elevated only through `/v1/admin/bootstrap`, using a deployment-provided secret. Bootstrap is allowed only while no administrator exists; there is no default production secret.

## Room and matchmaking model

Rooms are server records tied to published worlds. Joins are capacity-checked in a serializable transaction and return a short-lived, player/room-bound join ticket. Clients can heartbeat membership and use the authenticated hub to join/leave room event groups. A hosted cleanup service removes stale disconnected members, promotes or closes hostless rooms, and expires abandoned matchmaking requests using configurable timeouts. Matchmaking requests persist filters and are matched against available rooms or filled when a matching room is created. The REST join endpoint is the source of the join ticket even when a queue result arrives through SignalR.

The server accepts bounded JSON state proposals from the room host and persists sequence-numbered snapshots. It does not run an authoritative game simulation, physics engine, or anti-cheat model; applications requiring those guarantees need an application-specific simulation service.

## Storage and asset authorization

Uploads are size-limited and written beneath the configured asset root using server-generated storage keys; original names are metadata only. The owner must provide provenance and an authorization reference. A content hash detects corruption/duplicate bytes, and manifests are immutable version snapshots. Revoked assets are no longer delivered. This metadata workflow is not legal review or malware scanning; deployment operators must validate rights and integrate scanning before accepting public uploads.

## Deployment path

1. Run the smoke suite with a fresh SQLite database and validate the client contract.
2. For a shared server, configure PostgreSQL, secrets, persistent asset storage, TLS termination, explicit CORS origins, and backup retention.
3. Apply/review both providers' migrations in a staging environment before production deployment.
4. Restrict admin access at the network edge; keep the bootstrap secret out of source control and unset/remove it after the first admin is created.
5. Monitor `/health`, `/v1/admin/metrics`, application logs, disk capacity, and database backup restore tests.

## Public-launch gaps and scaling constraints

The backend is compile- and smoke-tested, but the following require deployment/app-specific work before a broad public service:

- TLS/reverse-proxy configuration, trusted forwarded-header policy, secret management, and a network-level admin allowlist.
- Backup scheduling, off-host encrypted copies, retention, and documented restore drills. The API exposes SQLite backup creation; PostgreSQL backup/restore is an operator responsibility (for example `pg_dump`/`pg_restore`).
- Malware scanning and/or quarantine for uploaded assets, CDN/object storage, storage quotas, and abuse controls.
- A real audio/media transport: current voice routes only issue authorization sessions.
- A production authoritative simulation/anti-cheat layer where game rules need server authority.
- Multi-instance real-time fanout and presence. `PresenceTracker` and default SignalR groups are in-process; use a Redis backplane or another managed/distributed SignalR topology and shared presence store before scaling horizontally.
- Load, soak, concurrency, and failover testing with the actual client and expected player counts. SQLite is for local/single-node use; use PostgreSQL for concurrent deployments.
- Reverse-proxy-aware rate-limit partitioning. The built-in limit uses the direct remote address; configure trusted proxy handling before relying on per-client IP quotas behind a proxy.
- Email verification, account recovery, privacy/retention workflows, full moderation operations, and formal incident response if required by the deployment.

The repository does not promise compatibility with an original service protocol unless a client-specific, authorized compatibility adapter is added and tested.
