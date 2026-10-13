# 2025/2026 Revival Backend Research Handoff

## Direct conclusion

A complete public specification for the private 2025 or 2026 game-client backend protocol was not found. Public creator documentation describes features available inside the original platform, but it does not publish a complete replacement-server contract containing every authentication, room, matchmaking, inventory, asset, real-time, voice, moderation, and telemetry message.

That means an untouched 2025 or 2026 client cannot be declared compatible with a replacement server based only on public web information. Exact compatibility requires an authorized protocol specification, an authorized client integration target, or a new/modified client that implements the independent contract in this repository.

The practical path is therefore:

```text
Independent client or authorized client build
        |
        | Project-owned HTTPS and real-time protocol
        v
Independent C#/.NET backend
        |
        +-- Project-owned database
        +-- Project-owned asset storage
        +-- Project-owned room and session services
```

The backend must not call the original service. It should own its own IDs, tokens, database records, asset URLs, room state, and event names.

## What public sources establish

### Creator assets are not automatically approved for a separate platform

The public asset-use policy defines first-party assets broadly, including code, scripts, circuits, characters, environments, items, audio, animations, avatar items, textures, weapons, and official rooms. It states that permitted first-party asset use is limited to content created and hosted within Rec Room and is not approved for third-party platforms. It also lists restricted categories such as partnership items, official contest rewards, moderator items, and certain original-game rewards.[1]

The project should therefore maintain an asset manifest with provenance and authorization for every file. The server should serve only files that the project is authorized to use. A private permission document may change the project-specific conclusion, but it should be retained with the project records and reviewed by the rights holder.

### Real-time communication can be project-owned

ASP.NET Core SignalR is designed for server-pushed real-time applications, including games and social networks. It manages connections, groups, client targeting, and hub methods. It supports WebSockets as the preferred transport, with server-sent events and long polling as fallbacks. It supports JSON and MessagePack hub protocols.[2]

For this backend, SignalR is suitable for presence, notifications, party changes, chat, room membership, and low-to-moderate-rate state events. Fast gameplay movement and physics should use a dedicated authoritative room simulation protocol rather than sending every transform through ordinary REST endpoints.

### Self-hosted persistence needs backups

PostgreSQL's documentation identifies SQL dumps, file-system-level backups, and continuous archiving as distinct backup approaches. A public deployment must choose a backup method, test restoration, protect backup files, and monitor failed jobs.[3]

SQLite is suitable for the first local installation because it avoids a second service. PostgreSQL is the better public-server database once multiple players and concurrent room writes are involved.

## What is not publicly established

The following information was not found as a complete authoritative public specification:

- Private 2025/2026 authentication request and response schemas
- Private token, entitlement, platform-account, or certificate requirements
- Exact original-client hostnames and endpoint lists
- Private room-instance and matchmaking wire protocols
- Movement, physics, replication, and authority rules
- Exact asset-manifest and bundle formats required by an untouched client
- Private inventory, catalog, progression, and economy schemas
- Private voice transport and moderation interfaces
- Complete version-negotiation rules between 2025 and 2026 clients
- Complete server error codes, headers, retry rules, and telemetry payloads

These are compatibility blockers, not details that should be guessed. A server can expose similarly named REST routes and still be incompatible if the original client expects different serialization, certificates, signatures, binary framing, or state transitions.

## Independent API surface

The project-owned contract is documented in `docs/API-CONTRACT.md`. The major groups are:

| Area | Project-owned responsibilities |
|---|---|
| Identity | Registration, login, logout, refresh, sessions, devices, and player IDs |
| Profiles | Display name, settings, avatar references, progression, and preferences |
| Social | Friends, requests, blocks, presence, notifications, and parties |
| Worlds | Discovery, creation, publishing, favorites, reviews, and ownership |
| Rooms | Room instances, joins, leaves, player lists, matchmaking, and lifecycle |
| Inventory | Catalog, items, entitlements, equipment, rewards, and progression |
| Communication | Conversations, messages, mute/block controls, and voice-session authorization |
| Safety | Reports, moderation actions, suspensions, audit logs, and administrator roles |
| Assets | Manifests, authorized uploads, content hashes, versions, and downloads |
| Operations | Health, metrics, migrations, backups, configuration, and recovery |

The HTTP API should be versioned under `/v1`. A real-time endpoint should authenticate with a project-issued bearer token and expose project-owned event names such as `presence.changed`, `party.updated`, `room.player_joined`, `room.state_changed`, `chat.message`, and `inventory.updated`.

## 2025/2026 compatibility strategy

### Recommended strategy: independent protocol

The client should send a startup handshake containing a project protocol version and client build version:

```json
{
  "clientVersion": "2026.1.0",
  "protocolVersion": 1,
  "platform": "windows"
}
```

The server should answer with supported features, limits, and required content-manifest versions:

```json
{
  "protocolVersion": 1,
  "serverVersion": "0.1.0",
  "features": ["presence", "parties", "rooms", "inventory"],
  "assetManifestVersion": "local-1"
}
```

This makes it possible to support multiple project client builds without depending on the original 2025 or 2026 server.

### Compatibility adapter strategy

If the authorized materials provide exact requirements for an original client, implement a separate adapter layer. The adapter should translate between the client-facing protocol and the project-owned domain model. It should not spread version-specific behavior through every service.

A compatibility adapter needs conformance tests for login, profile loading, room discovery, room joining, player synchronization, inventory loading, error behavior, and disconnect/reconnect handling. The adapter should be selected by a declared protocol version rather than guessing from arbitrary client traffic.

### What cannot be promised now

Without the authorized 2025/2026 protocol requirements, it is not possible to promise that an untouched original client will connect, authenticate, load rooms, or enter multiplayer sessions. The independent backend can be made complete for a client that implements the project-owned contract, but original-client compatibility remains a separate engineering target.

## Implementation plan

### Milestone 1: durable local core

Replace the current in-memory store with SQLite and a migration system. Add password hashing, refresh-token rotation, account lockout/rate limiting, profiles, player settings, and audit events. Add automated API tests for registration, login, token expiry, and unauthorized access.

### Milestone 2: social systems

Implement friend requests, blocks, presence, notifications, parties, and party invites. Persist all state transitions and publish real-time events through SignalR. Add reconnect behavior and idempotency keys so retrying a request does not duplicate a friendship or invite.

### Milestone 3: worlds and room instances

Add world ownership, publishing, discovery, favorites, reviews, room-instance records, join tickets, capacity limits, and lifecycle states. Keep the first room simulation authoritative on the server. Add a room heartbeat, player membership, reconnect grace period, and clean shutdown behavior.

### Milestone 4: matchmaking and synchronization

Add queues, filters, reservations, room allocation, and cancellation. Define a fixed tick rate and state snapshot format. Use sequence numbers, acknowledgements, interpolation on the client, and server-side validation. Do not trust client movement or inventory mutations.

### Milestone 5: catalog, inventory, and progression

Create project-owned item definitions, entitlement records, grants, removals, equipment state, rewards, and progression events. Every mutation should be transactional and auditable. Asset IDs must point to project-owned manifests and hashes.

### Milestone 6: chat, voice, and safety

Add text moderation, report workflows, mute/block enforcement, administrator roles, audit logs, and retention rules. Add a project-owned voice-session authorization layer. For media transport, choose either a self-hosted WebRTC media server or an in-process UDP service; do not place voice permissions in the client.

### Milestone 7: assets and creator content

Implement upload authorization, file-size limits, content hashing, manifest versions, resumable downloads, and asset revocation. Store provenance and authorization metadata. Do not ship restricted or unverified third-party content.

### Milestone 8: public self-hosting

Run the API and database on a self-managed server. Configure HTTPS, firewall rules, secrets, migrations, backups, restore tests, rate limits, logs, metrics, and crash recovery. Cloudflare is not required. A direct public IP and a domain with a valid certificate are sufficient, although the operator becomes responsible for network security and availability.

## Local-to-public deployment

For local testing, one computer runs the API and SQLite. The client connects to `http://127.0.0.1:<port>` on the same computer, or to the computer's private LAN address when testing with another device.

For a public test server, move the API and PostgreSQL database to a self-managed machine. Bind the API to the server's network interface, configure HTTPS, open only the application ports, and keep the database port private. Back up PostgreSQL regularly and test restoring it on a separate directory or machine.

Do not expose an administrative database port to the public internet. Do not use development registration, in-memory sessions, or unencrypted HTTP for a public deployment.

## Handoff checklist

Before calling the backend complete, the next developer should verify:

- The client implements the project-owned `/v1` contract.
- The client and server agree on JSON schemas, enum values, IDs, and error codes.
- Login, refresh, logout, expiry, and reconnect are tested.
- Two clients can discover a world, join the same room, see membership changes, and leave cleanly.
- Room state has server authority and sequence numbers.
- Inventory and progression mutations are transactional.
- Reports, mutes, blocks, and administrator actions are enforced server-side.
- Every shipped asset has provenance and authorization metadata.
- SQLite local setup and PostgreSQL public setup have migrations.
- Backups can be restored successfully.
- No production path calls an original service hostname or API.

## Files in this handoff

- `src/Revival.Api/` — current C# API starter
- `docs/API-CONTRACT.md` — project-owned endpoint and event catalog
- `docs/ARCHITECTURE.md` — runtime and deployment design
- `README.md` — local setup and current status
- `docker-compose.yml` — local published-service configuration

## References

[1]: https://recroom.com/using-rec-room-assets "Guidelines for Creator Use of Assets Built By Rec Room"
[2]: https://learn.microsoft.com/en-us/aspnet/core/signalr/introduction?view=aspnetcore-10.0 "Introduction to ASP.NET Core SignalR"
[3]: https://www.postgresql.org/docs/current/backup.html "PostgreSQL Backup and Restore"
