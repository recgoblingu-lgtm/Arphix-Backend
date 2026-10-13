# Revival Backend

A standalone C#/.NET 8 backend foundation for a new or authorized compatible social room client. The server is designed to use its own accounts, IDs, rooms, inventory, assets, and real-time events. It does not call the original service's servers or APIs.

## Current status

The first vertical slice is compiled and smoke-tested. It currently includes a health endpoint, development registration and login, sessions, world creation/listing, player search, an inventory read endpoint, and local packaging. The current store is in memory, so data resets when the process stops.

The complete independent route and event contract is documented in `docs/API-CONTRACT.md`. The deployment and module design is documented in `docs/ARCHITECTURE.md`.

## Recommended build order

The project will be completed in milestones so each part can be tested with a client. The next milestone is durable local persistence with SQLite, secure password handling, profiles, friends, presence, parties, and notifications. After that come room instances, matchmaking, real-time state synchronization, catalog/inventory, chat, moderation, asset delivery, administration, backups, and the public-server hardening pass.

A complete online platform cannot honestly be delivered as a single small scaffold: the client and server must agree on every data structure and real-time event, and each subsystem must be tested under multiple players. This repository is being built as an independent implementation rather than as a collection of calls to the original service.

## Run with the .NET SDK

```bash
dotnet run --project src/Revival.Api
```

Then open `http://localhost:5000/health` or the port shown by the application.

## Docker workflow

Build the application into `artifacts/`, then start the compose service:

```bash
dotnet publish src/Revival.Api -c Release -o artifacts
docker compose up
```

## Current endpoints

- `GET /health`
- `POST /v1/auth/register`
- `POST /v1/auth/login`
- `GET /v1/auth/me`
- `GET/POST /v1/worlds`
- `GET /v1/worlds/{id}`
- `GET /v1/social/players?q=...`
- `GET /v1/presence`
- `GET /v1/inventory`
