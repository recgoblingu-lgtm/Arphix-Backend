# Independent API Contract

All routes are versioned under `/v1`. Authentication uses a project-issued bearer token. Real-time traffic uses a project-issued SignalR/WebSocket session and the same bearer token.

## Identity and profiles

```text
POST   /auth/register
POST   /auth/login
POST   /auth/logout
POST   /auth/refresh
GET    /auth/me
GET    /players/{playerId}
PATCH  /players/{playerId}
GET    /players/{playerId}/settings
PUT    /players/{playerId}/settings
GET    /players/{playerId}/progression
```

## Social

```text
GET    /friends
POST   /friends/requests
POST   /friends/requests/{requestId}/accept
POST   /friends/requests/{requestId}/decline
DELETE /friends/{playerId}
POST   /players/{playerId}/block
DELETE /players/{playerId}/block
GET    /presence
GET    /notifications
POST   /notifications/{notificationId}/read
```

## Parties and rooms

```text
POST   /parties
GET    /parties/{partyId}
POST   /parties/{partyId}/members
DELETE /parties/{partyId}/members/{playerId}
POST   /parties/{partyId}/invites
POST   /parties/{partyId}/leave
GET    /worlds
POST   /worlds
GET    /worlds/{worldId}
PATCH  /worlds/{worldId}
DELETE /worlds/{worldId}
POST   /worlds/{worldId}/publish
POST   /worlds/{worldId}/unpublish
POST   /worlds/{worldId}/favorites
GET    /worlds/{worldId}/reviews
POST   /worlds/{worldId}/reviews
POST   /rooms
GET    /rooms/{roomId}
POST   /rooms/{roomId}/join
POST   /rooms/{roomId}/leave
GET    /rooms/{roomId}/players
POST   /matchmaking/requests
DELETE /matchmaking/requests/{requestId}
```

## Catalog, inventory, and progression

```text
GET    /catalog
GET    /catalog/{itemId}
GET    /inventory
POST   /inventory/equip
POST   /inventory/unequip
GET    /entitlements
GET    /progression
POST   /rewards/claim
```

## Messaging, voice, and safety

```text
GET    /chat/conversations
POST   /chat/conversations
GET    /chat/conversations/{conversationId}/messages
POST   /chat/conversations/{conversationId}/messages
DELETE /chat/messages/{messageId}
POST   /voice/rooms/{roomId}/join
POST   /voice/rooms/{roomId}/leave
POST   /reports
GET    /reports/{reportId}
POST   /players/{playerId}/mute
DELETE /players/{playerId}/mute
```

Voice transport is intentionally represented by a server-owned session contract. The actual media transport can be implemented as an in-process UDP service or a self-hosted WebRTC stack without relying on an outside provider.

## Asset delivery

```text
GET    /assets/manifests/{manifestId}
GET    /assets/{assetId}
POST   /worlds/{worldId}/assets
DELETE /worlds/{worldId}/assets/{assetId}
```

Assets must be supplied and used under the project's authorization. The backend stores and serves only project-owned or authorized files.

## Administration

```text
GET    /admin/health
GET    /admin/players
POST   /admin/players/{playerId}/suspend
POST   /admin/players/{playerId}/unsuspend
GET    /admin/reports
POST   /admin/reports/{reportId}/resolve
GET    /admin/audit-events
POST   /admin/catalog/items
PATCH  /admin/catalog/items/{itemId}
```

## Real-time event names

```text
session.ready
presence.changed
friend.requested
party.updated
room.player_joined
room.player_left
room.state_changed
chat.message
inventory.updated
notification.created
moderation.action
```

This is the project-owned interface. A client integration must implement this contract or an explicit compatibility adapter; the server does not call the original service.
