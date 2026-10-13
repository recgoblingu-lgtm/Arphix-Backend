# Revival Backend API Contract

The versioned base URL is `/v1`. All player and admin routes require `Authorization: Bearer <accessToken>` unless noted. IDs are project-owned UUIDs. Requests and responses use JSON and camelCase; asset uploads use multipart form data. The API does not accept original-service tokens or identifiers.

## Discovery and health

| Method | Path | Description |
|---|---|---|
| `GET` | `/health` | Database connectivity and service health |
| `GET` | `/v1` | Protocol version, feature flags, and configured limits |

## Identity and player profile

| Method | Path | Description |
|---|---|---|
| `POST` | `/v1/auth/register` | Create an account and issue access/refresh tokens |
| `POST` | `/v1/auth/login` | Authenticate and issue a token pair |
| `POST` | `/v1/auth/refresh` | Rotate a refresh token and its access token |
| `POST` | `/v1/auth/logout` | Revoke the current session |
| `GET` | `/v1/auth/me` | Current authenticated player |
| `GET` | `/v1/players/{playerId}` | Public profile fields |
| `PATCH` | `/v1/players/{playerId}` | Update own display name, bio, or avatar asset |
| `GET` / `PUT` | `/v1/players/{playerId}/settings` | Read or replace own client settings |
| `GET` | `/v1/players/{playerId}/progression` | Read own progression |
| `GET` | `/v1/players/search?q=...` | Search players |
| `GET` | `/v1/social/players?q=...` | Backward-compatible player-search alias |

Registration example:

```json
{
  "userName": "alice",
  "displayName": "Alice",
  "password": "use-a-unique-strong-password"
}
```

A successful register/login response includes `accessToken`, `refreshToken`, `tokenType`, `expiresAt`, `refreshExpiresAt`, and a public `player` object. Send access tokens in the Authorization header; send refresh tokens only to `/v1/auth/refresh`. Passwords are never returned.

## Social, presence, and notifications

| Method | Path | Description |
|---|---|---|
| `GET` | `/v1/friends` | List accepted friends and pending requests |
| `POST` | `/v1/friends/requests` | Send a request (`{"playerId":"<uuid>"}`) |
| `POST` | `/v1/friends/requests/{requestId}/accept` | Accept an incoming request |
| `POST` | `/v1/friends/requests/{requestId}/decline` | Decline an incoming request |
| `DELETE` | `/v1/friends/{playerId}` | Remove an accepted friend |
| `POST` / `DELETE` | `/v1/players/{playerId}/block` | Block or unblock a player |
| `POST` / `DELETE` | `/v1/players/{playerId}/mute` | Mute or unmute a player |
| `GET` | `/v1/presence` | Presence for the current player's friends |
| `GET` | `/v1/notifications?unreadOnly=true` | List own notifications |
| `POST` | `/v1/notifications/{notificationId}/read` | Mark a notification read |

## Parties

| Method | Path | Description |
|---|---|---|
| `POST` | `/v1/parties` | Create a party (`{"maxMembers":4}`) |
| `GET` | `/v1/parties/{partyId}` | Read party and membership |
| `POST` | `/v1/parties/{partyId}/members` | Add a member (`{"playerId":"<uuid>"}`) |
| `DELETE` | `/v1/parties/{partyId}/members/{playerId}` | Remove a member (leader or self as allowed) |
| `POST` | `/v1/parties/{partyId}/invites` | Invite a player (`{"playerId":"<uuid>"}`) |
| `POST` | `/v1/parties/{partyId}/invites/{inviteId}/accept` | Accept a party invite |
| `POST` | `/v1/parties/{partyId}/leave` | Leave a party |

## Worlds, rooms, and matchmaking

| Method | Path | Description |
|---|---|---|
| `GET` / `POST` | `/v1/worlds` | Discover published worlds or create a private world |
| `GET` / `PATCH` / `DELETE` | `/v1/worlds/{worldId}` | Read/update/delete a world owned by the caller |
| `POST` | `/v1/worlds/{worldId}/publish` | Publish an owned world |
| `POST` | `/v1/worlds/{worldId}/unpublish` | Unpublish an owned world |
| `POST` | `/v1/worlds/{worldId}/favorites` | Favorite/unfavorite a published world |
| `GET` / `POST` | `/v1/worlds/{worldId}/reviews` | List or submit a one-per-player rating/review |
| `POST` | `/v1/rooms` | Create a room for a published world |
| `GET` | `/v1/rooms/{roomId}` | Read room and available slots |
| `POST` | `/v1/rooms/{roomId}/join` | Join/rejoin and receive a short-lived join ticket |
| `POST` | `/v1/rooms/{roomId}/leave` | Leave; host departure promotes a successor or closes the room |
| `GET` | `/v1/rooms/{roomId}/players` | List active room membership |
| `POST` | `/v1/rooms/{roomId}/heartbeat` | Refresh the caller's room membership heartbeat |
| `POST` | `/v1/matchmaking/requests` | Match now or enter a persistent queue; supports `worldId`, `region`, `mode` |
| `GET` | `/v1/matchmaking/requests/{requestId}` | Poll the caller's matchmaking request |
| `DELETE` | `/v1/matchmaking/requests/{requestId}` | Cancel a searching request |

Example room/match filters:

```json
{"worldId":"<uuid>","capacity":8,"region":"us-west","mode":"casual"}
```

Room creation uses `worldId`, `capacity`, `region`, and `mode`. Matchmaking uses `worldId` (optional), `region`, and `mode`; unmatched requests remain searchable and are assigned as matching rooms appear. After a match, call the room join route to obtain a fresh join ticket.

## Catalog, inventory, and progression

| Method | Path | Description |
|---|---|---|
| `GET` | `/v1/catalog` | List active catalog items |
| `GET` | `/v1/catalog/{itemId}` | Read an active item |
| `GET` | `/v1/inventory` | Read own inventory and equipment state |
| `POST` | `/v1/inventory/equip` | Equip an owned entry (`{"inventoryId":"<uuid>"}`) |
| `POST` | `/v1/inventory/unequip` | Unequip an owned entry |
| `GET` | `/v1/entitlements` | Read own active entitlements |
| `GET` | `/v1/progression` | Read own progression values |
| `POST` | `/v1/rewards/claim` | Claim a supported reward once (`{"rewardId":"welcome"}` or `daily`) |

The first-run catalog seed contains a project-owned `welcome-badge`. Admins can add catalog entries and grant inventory; client code cannot mint inventory by editing a local file.

## Chat, voice authorization, and reports

| Method | Path | Description |
|---|---|---|
| `GET` / `POST` | `/v1/chat/conversations` | List or create a private conversation (`{"playerIds":["<uuid>"]}`) |
| `GET` / `POST` | `/v1/chat/conversations/{conversationId}/messages` | Read paginated messages or send `{"text":"..."}` |
| `DELETE` | `/v1/chat/messages/{messageId}` | Delete a message when authorized |
| `POST` | `/v1/voice/rooms/{roomId}/join` | Issue a short-lived room-bound voice authorization session |
| `POST` | `/v1/voice/rooms/{roomId}/leave` | End that voice session |
| `POST` | `/v1/reports` | Submit a player/world/message report |
| `GET` | `/v1/reports/{reportId}` | Read the reporter's report status |

Voice routes issue authorization credentials only. No audio media transport is included.

## Assets

| Method | Path | Description |
|---|---|---|
| `POST` | `/v1/worlds/{worldId}/assets` | Owner upload (`File`, `Provenance`, `AuthorizationReference` multipart fields) |
| `GET` | `/v1/assets/manifests/{manifestId}` | Read an immutable manifest version |
| `GET` | `/v1/assets/{assetId}` | Download an authorized, non-revoked asset |
| `DELETE` | `/v1/worlds/{worldId}/assets/{assetId}` | Revoke an asset as its world owner or an admin |

Files are limited to 50 MiB per upload. Original filenames are metadata only; storage keys are server-generated. The caller is responsible for having the rights represented by the provenance/authorization fields.

## Administration

| Method | Path | Description |
|---|---|---|
| `POST` | `/v1/admin/bootstrap` | One-time first-admin claim using `X-Admin-Bootstrap` secret; no bearer required |
| `GET` | `/v1/admin/health` | Admin database health details |
| `GET` | `/v1/admin/metrics` | Operational counts |
| `GET` | `/v1/admin/players` | Search/list players |
| `POST` | `/v1/admin/players/{playerId}/suspend` | Suspend and revoke active sessions |
| `POST` | `/v1/admin/players/{playerId}/unsuspend` | Remove suspension |
| `POST` | `/v1/admin/players/{playerId}/inventory` | Grant catalog-backed inventory (`definitionId`, optional `quantity`, `source`) |
| `GET` | `/v1/admin/reports` | Moderation queue |
| `POST` | `/v1/admin/reports/{reportId}/resolve` | Resolve/dismiss a report with an audit record |
| `GET` | `/v1/admin/audit-events` | Read the audit log |
| `POST` | `/v1/admin/catalog/items` | Create a catalog definition |
| `PATCH` | `/v1/admin/catalog/items/{itemId}` | Update a catalog definition |
| `POST` | `/v1/admin/backups` | Create an online SQLite backup; PostgreSQL backup is operator-managed |

## SignalR real-time contract

Connect to `/v1/realtime` using SignalR JSON protocol. Authenticate the negotiate request with `Authorization: Bearer <accessToken>`; browser WebSocket clients may pass `access_token` as a query parameter on the WebSocket connection. Hub methods:

- `JoinRoom(roomId)` — requires persisted room membership and sends the current room snapshot to the caller.
- `LeaveRoom(roomId)` — removes the connection from that room group.
- `PublishRoomState(roomId, sequence, state)` — host-only, increasing sequence, bounded JSON snapshot proposal.

Server-to-client event names:

```text
session.ready              presence.changed
friend.requested           party.updated
room.player_joined         room.player_left
room.state_changed         matchmaking.matched
matchmaking.expired
chat.message               inventory.updated
notification.created       moderation.action
```

Payloads are JSON objects. Treat each event name as a versioned project protocol; a client and server release must agree on the payload shape. Realtime room state is not a server-side game simulation.

## Status and errors

Typical responses include `400` invalid payload, `401` missing/invalid/expired session, `403` authorization or policy failure, `404` missing/non-visible resource, `409` state conflict/duplicate, `429` rate limit or timed account lock, and `500` generic `{"error":"internal_server_error","traceId":"..."}`. Do not expose internal exception messages to clients.
