using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Revival.Api.Data;

public abstract class AppDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<Player> Players => Set<Player>();
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();
    public DbSet<LoginAttempt> LoginAttempts => Set<LoginAttempt>();
    public DbSet<World> Worlds => Set<World>();
    public DbSet<FriendRequest> FriendRequests => Set<FriendRequest>();
    public DbSet<Friendship> Friendships => Set<Friendship>();
    public DbSet<PlayerBlock> Blocks => Set<PlayerBlock>();
    public DbSet<PlayerMute> Mutes => Set<PlayerMute>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Party> Parties => Set<Party>();
    public DbSet<PartyMember> PartyMembers => Set<PartyMember>();
    public DbSet<PartyInvite> PartyInvites => Set<PartyInvite>();
    public DbSet<WorldFavorite> WorldFavorites => Set<WorldFavorite>();
    public DbSet<WorldReview> WorldReviews => Set<WorldReview>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<RoomMember> RoomMembers => Set<RoomMember>();
    public DbSet<RoomJoinTicket> RoomJoinTickets => Set<RoomJoinTicket>();
    public DbSet<MatchmakingRequest> MatchmakingRequests => Set<MatchmakingRequest>();
    public DbSet<CatalogItem> CatalogItems => Set<CatalogItem>();
    public DbSet<InventoryEntry> Inventory => Set<InventoryEntry>();
    public DbSet<Entitlement> Entitlements => Set<Entitlement>();
    public DbSet<RewardClaim> RewardClaims => Set<RewardClaim>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMember> ConversationMembers => Set<ConversationMember>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<ModerationReport> Reports => Set<ModerationReport>();
    public DbSet<WorldAsset> Assets => Set<WorldAsset>();
    public DbSet<AssetManifest> AssetManifests => Set<AssetManifest>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<VoiceSession> VoiceSessions => Set<VoiceSession>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var utcTicks = new ValueConverter<DateTimeOffset, long>(value => value.UtcDateTime.Ticks, value => new DateTimeOffset(value, TimeSpan.Zero));
        foreach (var property in model.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties())
                     .Where(property => property.ClrType == typeof(DateTimeOffset) || Nullable.GetUnderlyingType(property.ClrType) == typeof(DateTimeOffset)))
            property.SetValueConverter(utcTicks);

        model.Entity<Player>().HasKey(x => x.Id);
        model.Entity<Player>().HasIndex(x => x.UserNameNormalized).IsUnique();
        model.Entity<AuthSession>().HasKey(x => x.Id);
        model.Entity<AuthSession>().HasIndex(x => x.AccessTokenHash).IsUnique();
        model.Entity<AuthSession>().HasIndex(x => x.RefreshTokenHash).IsUnique();
        model.Entity<AuthSession>().HasIndex(x => new { x.PlayerId, x.RefreshExpiresAt });
        model.Entity<LoginAttempt>().HasKey(x => x.UserNameNormalized);
        model.Entity<World>().HasIndex(x => new { x.IsPublished, x.UpdatedAt });
        model.Entity<FriendRequest>().HasIndex(x => new { x.FromPlayerId, x.ToPlayerId, x.Status });
        model.Entity<Friendship>().HasKey(x => new { x.PlayerAId, x.PlayerBId });
        model.Entity<PlayerBlock>().HasKey(x => new { x.PlayerId, x.BlockedPlayerId });
        model.Entity<PlayerMute>().HasKey(x => new { x.PlayerId, x.MutedPlayerId });
        model.Entity<Notification>().HasIndex(x => new { x.PlayerId, x.CreatedAt });
        model.Entity<PartyMember>().HasKey(x => new { x.PartyId, x.PlayerId });
        model.Entity<PartyInvite>().HasIndex(x => new { x.PartyId, x.InviteeId, x.Status });
        model.Entity<WorldFavorite>().HasKey(x => new { x.WorldId, x.PlayerId });
        model.Entity<WorldReview>().HasKey(x => new { x.WorldId, x.PlayerId });
        model.Entity<Room>().HasIndex(x => new { x.WorldId, x.Status, x.CreatedAt });
        model.Entity<RoomMember>().HasKey(x => new { x.RoomId, x.PlayerId });
        model.Entity<RoomJoinTicket>().HasIndex(x => x.TicketHash).IsUnique();
        model.Entity<MatchmakingRequest>().HasIndex(x => new { x.PlayerId, x.Status });
        model.Entity<CatalogItem>().HasKey(x => x.Id);
        model.Entity<InventoryEntry>().HasIndex(x => new { x.PlayerId, x.DefinitionId });
        model.Entity<Entitlement>().HasIndex(x => new { x.PlayerId, x.DefinitionId, x.RevokedAt }).IsUnique();
        model.Entity<RewardClaim>().HasIndex(x => new { x.PlayerId, x.ClaimKey }).IsUnique();
        model.Entity<ConversationMember>().HasKey(x => new { x.ConversationId, x.PlayerId });
        model.Entity<ChatMessage>().HasIndex(x => new { x.ConversationId, x.CreatedAt });
        model.Entity<ModerationReport>().HasIndex(x => new { x.Status, x.CreatedAt });
        model.Entity<WorldAsset>().HasIndex(x => new { x.WorldId, x.IsRevoked });
        model.Entity<AssetManifest>().HasIndex(x => new { x.WorldId, x.Version }).IsUnique();
        model.Entity<AuditEvent>().HasIndex(x => x.CreatedAt);
        model.Entity<VoiceSession>().HasIndex(x => new { x.RoomId, x.PlayerId, x.EndedAt });

        model.Entity<Player>().Property(x => x.UserName).HasMaxLength(32);
        model.Entity<Player>().Property(x => x.UserNameNormalized).HasMaxLength(32);
        model.Entity<Player>().Property(x => x.DisplayName).HasMaxLength(48);
        model.Entity<World>().Property(x => x.Name).HasMaxLength(80);
        model.Entity<World>().Property(x => x.Description).HasMaxLength(2000);
        model.Entity<ChatMessage>().Property(x => x.Text).HasMaxLength(4000);
        model.Entity<ModerationReport>().Property(x => x.Description).HasMaxLength(2000);
        model.Entity<WorldAsset>().Property(x => x.OriginalName).HasMaxLength(255);
    }
}
