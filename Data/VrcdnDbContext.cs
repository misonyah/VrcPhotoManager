using Microsoft.EntityFrameworkCore;
using VrcPhotoManager.Models;

namespace VrcPhotoManager.Data;

public class VrcdnDbContext : DbContext
{
    public DbSet<Photo> Photos => Set<Photo>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();
    public DbSet<RegisteredPerson> RegisteredPeople => Set<RegisteredPerson>();
    public DbSet<PersonReferencePhoto> PersonReferencePhotos => Set<PersonReferencePhoto>();
    public DbSet<DetectedFace> DetectedFaces => Set<DetectedFace>();
    public DbSet<FaceLabel> FaceLabels => Set<FaceLabel>();
    public DbSet<SuggestionLog> SuggestionLogs => Set<SuggestionLog>();
    public DbSet<PhotoPlayer> PhotoPlayers => Set<PhotoPlayer>();
    public DbSet<GamelogInferredPlayer> GamelogInferredPlayers => Set<GamelogInferredPlayer>();
    public DbSet<KnownVrcUser> KnownVrcUsers => Set<KnownVrcUser>();
    public DbSet<VrcUserAlias> VrcUserAliases => Set<VrcUserAlias>();
    public DbSet<AvatarRegion> AvatarRegions => Set<AvatarRegion>();
    public DbSet<AvatarCatalog> AvatarCatalogs => Set<AvatarCatalog>();
    public DbSet<Library> Libraries => Set<Library>();

    private readonly string _dbPath;

    public VrcdnDbContext(string dbPath)
    {
        _dbPath = dbPath;
    }

    // "Default Timeout" (seconds) maps to sqlite3_busy_timeout - without it, a write from one
    // repository class's own DbContext (PhotoRepository/FaceRepository/AvatarRegionRepository
    // all point at the same file, each with their own short-lived connections) that lands
    // while another one is mid-transaction fails immediately as "database is locked" instead
    // of waiting briefly and retrying. Found via a real "An error occurred while saving the
    // entity changes" report that coincided with a concurrent diagnostic CLI run
    // (--test-vrcdn-sync) against the same live database file.
    protected override void OnConfiguring(DbContextOptionsBuilder options) =>
        options.UseSqlite($"Data Source={_dbPath};Default Timeout=10");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Photo>(entity =>
        {
            entity.ToTable("photos");
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => p.LocalPath).IsUnique();
            entity.HasIndex(p => p.RemoteSourceId).IsUnique().HasFilter("remote_source_id IS NOT NULL");
            entity.HasIndex(p => p.RemoteStatus);

            entity.Property(p => p.Id).HasColumnName("id");
            entity.Property(p => p.LocalPath).HasColumnName("local_path");
            entity.Property(p => p.FileSize).HasColumnName("file_size");
            entity.Property(p => p.Mtime).HasColumnName("mtime");
            entity.Property(p => p.Width).HasColumnName("width");
            entity.Property(p => p.Height).HasColumnName("height");
            entity.Property(p => p.FileHash).HasColumnName("file_hash");
            entity.Property(p => p.Thumbnail).HasColumnName("thumbnail");
            entity.Property(p => p.Rating).HasColumnName("rating");
            entity.Property(p => p.AvatarType).HasColumnName("avatar_type");
            entity.Property(p => p.AvatarTypeConfidence).HasColumnName("avatar_type_confidence");
            entity.Property(p => p.AvatarCatalogId).HasColumnName("avatar_catalog_id");
            entity.Property(p => p.LibraryId).HasColumnName("library_id");
            entity.Property(p => p.MetadataScanned).HasColumnName("metadata_scanned");
            entity.Property(p => p.FacesScanned).HasColumnName("faces_scanned");
            entity.Property(p => p.AuthorId).HasColumnName("author_id");
            entity.Property(p => p.AuthorDisplayName).HasColumnName("author_display_name");
            entity.Property(p => p.WorldName).HasColumnName("world_name");
            entity.Property(p => p.WorldId).HasColumnName("world_id");
            entity.Property(p => p.WorldNameInferred).HasColumnName("world_name_inferred");
            entity.Property(p => p.Selected).HasColumnName("selected");
            entity.Property(p => p.RemoteStatus).HasColumnName("remote_status").HasConversion<string>();
            entity.Property(p => p.RemoteUrl).HasColumnName("remote_url");
            entity.Property(p => p.RemoteId).HasColumnName("remote_id");
            entity.Property(p => p.UploadedAt).HasColumnName("uploaded_at");
            entity.Property(p => p.UploadCropMode).HasColumnName("upload_crop_mode");
            entity.Property(p => p.UploadedFormat).HasColumnName("uploaded_format");
            entity.Property(p => p.UploadedOffsetX).HasColumnName("uploaded_offset_x");
            entity.Property(p => p.UploadedOffsetY).HasColumnName("uploaded_offset_y");
            entity.Property(p => p.CropOffsetX).HasColumnName("crop_offset_x");
            entity.Property(p => p.CropOffsetY).HasColumnName("crop_offset_y");
            entity.Property(p => p.CropRatioOverride).HasColumnName("crop_ratio_override");
            entity.Property(p => p.PendingRemovalRemoteId).HasColumnName("pending_removal_remote_id");
            entity.Property(p => p.RemoteSourceUrl).HasColumnName("remote_source_url");
            entity.Property(p => p.RemoteSourceId).HasColumnName("remote_source_id");
            entity.Property(p => p.LastAccessedAt).HasColumnName("last_accessed_at");
        });

        modelBuilder.Entity<AppSetting>(entity =>
        {
            entity.ToTable("settings");
            entity.HasKey(s => s.Key);
            entity.Property(s => s.Key).HasColumnName("key");
            entity.Property(s => s.Value).HasColumnName("value");
        });

        modelBuilder.Entity<RegisteredPerson>(entity =>
        {
            entity.ToTable("registered_people");
            entity.HasKey(p => p.Id);
            // VRChat display names are NOT globally unique across accounts, and this app now
            // auto-creates people from real VRC accounts (FindOrCreatePersonByVrcUserId) - a
            // unique constraint on Name alone would let two different real accounts collide.
            // VrcUserId is the real identity key; it's only unique when present (a free-text
            // "no VRC id" person and a VRC-linked person are allowed to share a Name).
            entity.HasIndex(p => p.VrcUserId).IsUnique().HasFilter("vrc_user_id IS NOT NULL");

            entity.Property(p => p.Id).HasColumnName("id");
            entity.Property(p => p.Name).HasColumnName("name").IsRequired();
            entity.Property(p => p.CreatedAt).HasColumnName("created_at");
            entity.Property(p => p.VrcUserId).HasColumnName("vrc_user_id");
            entity.Property(p => p.VrcProfileThumbnail).HasColumnName("vrc_profile_thumbnail");
            entity.Property(p => p.VrcProfileThumbnailFetchedAt).HasColumnName("vrc_profile_thumbnail_fetched_at");
        });

        modelBuilder.Entity<PersonReferencePhoto>(entity =>
        {
            entity.ToTable("person_reference_photos");
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => new { p.PersonId, p.PhotoId }).IsUnique();

            entity.Property(p => p.Id).HasColumnName("id");
            entity.Property(p => p.PersonId).HasColumnName("person_id");
            entity.Property(p => p.PhotoId).HasColumnName("photo_id");
            entity.Property(p => p.Source).HasColumnName("source").HasConversion<string>();
            entity.Property(p => p.AddedAt).HasColumnName("added_at");
        });

        modelBuilder.Entity<DetectedFace>(entity =>
        {
            entity.ToTable("detected_faces");
            entity.HasKey(f => f.Id);
            entity.HasIndex(f => f.PhotoId);

            entity.Property(f => f.Id).HasColumnName("id");
            entity.Property(f => f.PhotoId).HasColumnName("photo_id");
            entity.Property(f => f.X).HasColumnName("x");
            entity.Property(f => f.Y).HasColumnName("y");
            entity.Property(f => f.Width).HasColumnName("width");
            entity.Property(f => f.Height).HasColumnName("height");
            entity.Property(f => f.Embedding).HasColumnName("embedding");
            entity.Property(f => f.DetectedAt).HasColumnName("detected_at");
            entity.Property(f => f.Deleted).HasColumnName("deleted").HasDefaultValue(false);
        });

        modelBuilder.Entity<AvatarRegion>(entity =>
        {
            entity.ToTable("avatar_regions");
            entity.HasKey(r => r.Id);
            entity.HasIndex(r => r.PhotoId);

            entity.Property(r => r.Id).HasColumnName("id");
            entity.Property(r => r.PhotoId).HasColumnName("photo_id");
            entity.Property(r => r.X).HasColumnName("x");
            entity.Property(r => r.Y).HasColumnName("y");
            entity.Property(r => r.Width).HasColumnName("width");
            entity.Property(r => r.Height).HasColumnName("height");
            entity.Property(r => r.AvatarCatalogId).HasColumnName("avatar_catalog_id");
            entity.Property(r => r.AvatarDisplayName).HasColumnName("avatar_display_name");
            entity.Property(r => r.TaggedAt).HasColumnName("tagged_at");
            entity.Property(r => r.Deleted).HasColumnName("deleted").HasDefaultValue(false);
            entity.Property(r => r.Confirmed).HasColumnName("confirmed").HasDefaultValue(true);
            entity.Property(r => r.Confidence).HasColumnName("confidence");
        });

        modelBuilder.Entity<FaceLabel>(entity =>
        {
            entity.ToTable("face_labels");
            entity.HasKey(l => l.Id);
            entity.HasIndex(l => l.DetectedFaceId);
            entity.HasIndex(l => l.PersonId);

            entity.Property(l => l.Id).HasColumnName("id");
            entity.Property(l => l.DetectedFaceId).HasColumnName("detected_face_id");
            entity.Property(l => l.PersonId).HasColumnName("person_id");
            entity.Property(l => l.Confidence).HasColumnName("confidence");
            entity.Property(l => l.Source).HasColumnName("source").HasConversion<string>();
            entity.Property(l => l.Confirmed).HasColumnName("confirmed");
            entity.Property(l => l.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<SuggestionLog>(entity =>
        {
            entity.ToTable("suggestion_logs");
            entity.HasKey(s => s.Id);
            entity.HasIndex(s => s.DetectedFaceId);

            entity.Property(s => s.Id).HasColumnName("id");
            entity.Property(s => s.DetectedFaceId).HasColumnName("detected_face_id");
            entity.Property(s => s.SuggestedPersonId).HasColumnName("suggested_person_id");
            entity.Property(s => s.CombinedScore).HasColumnName("combined_score");
            entity.Property(s => s.FaceSimilarityScore).HasColumnName("face_similarity_score");
            entity.Property(s => s.AvatarAffinityBoost).HasColumnName("avatar_affinity_boost");
            entity.Property(s => s.CoOccurrenceBoost).HasColumnName("co_occurrence_boost");
            entity.Property(s => s.Tier).HasColumnName("tier").HasConversion<string>();
            entity.Property(s => s.CreatedAt).HasColumnName("created_at");
            entity.Property(s => s.Outcome).HasColumnName("outcome").HasConversion<string>();
            entity.Property(s => s.OutcomeAt).HasColumnName("outcome_at");
        });

        modelBuilder.Entity<PhotoPlayer>(entity =>
        {
            entity.ToTable("photo_players");
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => p.PhotoId);
            entity.HasIndex(p => p.UserId);

            entity.Property(p => p.Id).HasColumnName("id");
            entity.Property(p => p.PhotoId).HasColumnName("photo_id");
            entity.Property(p => p.UserId).HasColumnName("user_id").IsRequired();
            entity.Property(p => p.DisplayName).HasColumnName("display_name").IsRequired();
        });

        modelBuilder.Entity<GamelogInferredPlayer>(entity =>
        {
            entity.ToTable("gamelog_inferred_players");
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => p.PhotoId);
            entity.HasIndex(p => p.UserId);

            entity.Property(p => p.Id).HasColumnName("id");
            entity.Property(p => p.PhotoId).HasColumnName("photo_id");
            entity.Property(p => p.UserId).HasColumnName("user_id").IsRequired();
            entity.Property(p => p.DisplayName).HasColumnName("display_name").IsRequired();
        });

        modelBuilder.Entity<KnownVrcUser>(entity =>
        {
            entity.ToTable("known_vrc_users");
            entity.HasKey(u => u.UserId);

            entity.Property(u => u.UserId).HasColumnName("user_id");
            entity.Property(u => u.DisplayName).HasColumnName("display_name").IsRequired();
            entity.Property(u => u.LastSeenAt).HasColumnName("last_seen_at");
        });

        modelBuilder.Entity<AvatarCatalog>(entity =>
        {
            entity.ToTable("avatar_catalog");
            entity.HasKey(c => c.Id);
            entity.HasIndex(c => c.TrainedCatalogId).IsUnique().HasFilter("trained_catalog_id IS NOT NULL");
            entity.HasIndex(c => c.BoothProduct).IsUnique().HasFilter("booth_product IS NOT NULL");
            entity.HasIndex(c => new { c.GumroadUser, c.GumroadProduct }).IsUnique()
                .HasFilter("gumroad_user IS NOT NULL AND gumroad_product IS NOT NULL");
            entity.HasIndex(c => new { c.JinxxyUser, c.JinxxyProduct }).IsUnique()
                .HasFilter("jinxxy_user IS NOT NULL AND jinxxy_product IS NOT NULL");

            entity.Property(c => c.Id).HasColumnName("id");
            entity.Property(c => c.TrainedCatalogId).HasColumnName("trained_catalog_id");
            entity.Property(c => c.DisplayName).HasColumnName("display_name");
            entity.Property(c => c.BoothProduct).HasColumnName("booth_product");
            entity.Property(c => c.GumroadUser).HasColumnName("gumroad_user");
            entity.Property(c => c.GumroadProduct).HasColumnName("gumroad_product");
            entity.Property(c => c.JinxxyUser).HasColumnName("jinxxy_user");
            entity.Property(c => c.JinxxyProduct).HasColumnName("jinxxy_product");
            entity.Property(c => c.ParentItemId).HasColumnName("parent_item_id");
            entity.Property(c => c.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<Library>(entity =>
        {
            entity.ToTable("libraries");
            entity.HasKey(l => l.Id);

            entity.Property(l => l.Id).HasColumnName("id");
            entity.Property(l => l.Type).HasColumnName("type").HasConversion<string>();
            entity.Property(l => l.DisplayName).HasColumnName("display_name").IsRequired();
            entity.Property(l => l.LocalPath).HasColumnName("local_path");
            entity.Property(l => l.DiscordGuildId).HasColumnName("discord_guild_id");
            entity.Property(l => l.DiscordChannelId).HasColumnName("discord_channel_id");
            entity.Property(l => l.LastSyncedAt).HasColumnName("last_synced_at");
            entity.Property(l => l.LastSyncedMessageId).HasColumnName("last_synced_message_id");
            entity.Property(l => l.AutoDownloadOriginals).HasColumnName("auto_download_originals").HasDefaultValue(false);
            entity.Property(l => l.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<VrcUserAlias>(entity =>
        {
            entity.ToTable("vrc_user_aliases");
            entity.HasKey(a => a.Id);
            entity.HasIndex(a => a.UserId);
            // Prevents the same alias string being recorded twice for the same user - both
            // the automatic-capture upsert and a manual add key off this to detect "already
            // have it" without a separate lookup query.
            entity.HasIndex(a => new { a.UserId, a.Alias }).IsUnique();

            entity.Property(a => a.Id).HasColumnName("id");
            entity.Property(a => a.UserId).HasColumnName("user_id").IsRequired();
            entity.Property(a => a.Alias).HasColumnName("alias").IsRequired();
            entity.Property(a => a.Source).HasColumnName("source").HasConversion<string>();
            entity.Property(a => a.AddedAt).HasColumnName("added_at");
        });
    }
}
