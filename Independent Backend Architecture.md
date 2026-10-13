# Independent Backend Architecture

This project is a standalone backend for a new or authorized compatible client. It does not depend on the original service's servers, APIs, authentication, storage, or live game services.

## Runtime

- C# and .NET 8
- ASP.NET Core HTTP API
- SignalR/WebSockets for real-time events
- SQLite for the first local installation
- PostgreSQL as the public-server database option
- Local filesystem storage for development assets and uploads

## Core services

The first deployment keeps these modules in one process so the project is easy to run locally:

- Identity and sessions
- Profiles and player settings
- Friends, blocks, presence, parties, and notifications
- Worlds, publishing, favorites, reviews, room instances, and matchmaking
- Inventory, catalog, entitlements, rewards, and progression
- Chat and moderation reports
- Asset manifests and authorized file delivery
- Administration, audit events, health, metrics, and backups

## Independent client contract

The client must target this server's base URL and use the versioned `/v1` contract. It must not assume that an original service hostname, token, asset URL, or proprietary response format exists.

The server owns player IDs, world IDs, room IDs, inventory IDs, session tokens, and event names. The client receives these from the API and persists only identifiers that belong to this project.

## Local-to-public path

1. Run the API and SQLite on one computer.
2. Connect the client over localhost or the local network.
3. Add automated tests and backups.
4. Move the API and database to a self-managed server.
5. Open only the required HTTPS and real-time ports.
6. Replace SQLite with PostgreSQL when multiple players need reliable concurrent access.

## Production requirements before public access

- Password hashing or external identity integration
- Secure refresh-token rotation
- HTTPS certificates
- Rate limiting and request validation
- Database migrations and scheduled backups
- Upload size limits and malware scanning
- Moderation permissions and audit logs
- Firewall rules and secret management
- Monitoring and crash recovery
