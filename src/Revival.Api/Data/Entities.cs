namespace Revival.Api.Data;

public sealed class Player
{
    public Guid Id { get; set; }
    public string UserName { get; set; } = "";
    public string UserNameNormalized { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Bio { get; set; } = "";
    public Guid? AvatarAssetId { get; set; }
    public string PasswordHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public bool IsAdmin { get; set; }
    public bool IsSuspended { get; set; }
    public DateTimeOffset? SuspendedUntil { get; set; }
    public string? SuspensionReason { get; set; }
    public string SettingsJson { get; set; } = "{}";
    public string ProgressionJson { get; set; } = "{}";
    public DateTimeOffset? LastSeenAt { get; set; }
}

public sealed class AuthSession
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }
    public string AccessTokenHash { get; set; } = "";
    public string RefreshTokenHash { get; set; } = "";
    public DateTimeOffset AccessExpiresAt { get; set; }
    public DateTimeOffset RefreshExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedBySessionId { get; set; }
}

public sealed class LoginAttempt
{
    public string UserNameNormalized { get; set; } = "";
    public int FailureCount { get; set; }
    public DateTimeOffset WindowStartedAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
}

public sealed class World
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string TagsJson { get; set; } = "[]";
    public bool IsPublished { get; set; }
    public int MaxPlayers { get; set; } = 16;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class FriendRequest
{
    public Guid Id { get; set; }
    public Guid FromPlayerId { get; set; }
    public Guid ToPlayerId { get; set; }
    public string Status { get; set; } = "pending";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
}

public sealed class Friendship
{
    public Guid PlayerAId { get; set; }
    public Guid PlayerBId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PlayerBlock
{
    public Guid PlayerId { get; set; }
    public Guid BlockedPlayerId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PlayerMute
{
    public Guid PlayerId { get; set; }
    public Guid MutedPlayerId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Notification
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }
    public string Type { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

public sealed class Party
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public int MaxMembers { get; set; } = 6;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PartyMember
{
    public Guid PartyId { get; set; }
    public Guid PlayerId { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
}

public sealed class PartyInvite
{
    public Guid Id { get; set; }
    public Guid PartyId { get; set; }
    public Guid InviterId { get; set; }
    public Guid InviteeId { get; set; }
    public string Status { get; set; } = "pending";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class WorldFavorite
{
    public Guid WorldId { get; set; }
    public Guid PlayerId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class WorldReview
{
    public Guid WorldId { get; set; }
    public Guid PlayerId { get; set; }
    public int Rating { get; set; }
    public string Text { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Room
{
    public Guid Id { get; set; }
    public Guid WorldId { get; set; }
    public Guid HostId { get; set; }
    public string Region { get; set; } = "local";
    public string Mode { get; set; } = "default";
    public string Status { get; set; } = "open";
    public int Capacity { get; set; } = 16;
    public long StateSequence { get; set; }
    public string StateJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class RoomMember
{
    public Guid RoomId { get; set; }
    public Guid PlayerId { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset LastHeartbeatAt { get; set; }
}

public sealed class RoomJoinTicket
{
    public Guid Id { get; set; }
    public Guid RoomId { get; set; }
    public Guid PlayerId { get; set; }
    public string TicketHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RedeemedAt { get; set; }
}

public sealed class MatchmakingRequest
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }
    public Guid? WorldId { get; set; }
    public string? Region { get; set; }
    public string? Mode { get; set; }
    public string Status { get; set; } = "searching";
    public Guid? RoomId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class CatalogItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string? EquipmentSlot { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class InventoryEntry
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }
    public string DefinitionId { get; set; } = "";
    public int Quantity { get; set; } = 1;
    public bool IsEquipped { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
}

public sealed class Entitlement
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }
    public string DefinitionId { get; set; } = "";
    public string Source { get; set; } = "";
    public DateTimeOffset GrantedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class RewardClaim
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }
    public string ClaimKey { get; set; } = "";
    public string RewardJson { get; set; } = "{}";
    public DateTimeOffset ClaimedAt { get; set; }
}

public sealed class Conversation
{
    public Guid Id { get; set; }
    public Guid CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ConversationMember
{
    public Guid ConversationId { get; set; }
    public Guid PlayerId { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
}

public sealed class ChatMessage
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid SenderId { get; set; }
    public string Text { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class ModerationReport
{
    public Guid Id { get; set; }
    public Guid ReporterId { get; set; }
    public Guid? TargetPlayerId { get; set; }
    public Guid? TargetWorldId { get; set; }
    public Guid? TargetMessageId { get; set; }
    public string Category { get; set; } = "other";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "open";
    public Guid? ResolvedById { get; set; }
    public string? Resolution { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}

public sealed class WorldAsset
{
    public Guid Id { get; set; }
    public Guid WorldId { get; set; }
    public Guid OwnerId { get; set; }
    public Guid ManifestId { get; set; }
    public string OriginalName { get; set; } = "";
    public string StorageKey { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string Provenance { get; set; } = "";
    public string AuthorizationReference { get; set; } = "";
    public bool IsRevoked { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AssetManifest
{
    public Guid Id { get; set; }
    public Guid WorldId { get; set; }
    public int Version { get; set; }
    public string AssetIdsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AuditEvent
{
    public Guid Id { get; set; }
    public Guid? ActorId { get; set; }
    public string Action { get; set; } = "";
    public string TargetType { get; set; } = "";
    public string TargetId { get; set; } = "";
    public string DetailsJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class VoiceSession
{
    public Guid Id { get; set; }
    public Guid RoomId { get; set; }
    public Guid PlayerId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
}
