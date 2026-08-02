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
    public DbSet<PhotoPlayer> PhotoPlayers => Set<PhotoPlayer>();
    public DbSet<GamelogInferredPlayer> GamelogInferredPlayers => Set<GamelogInferredPlayer>();
    public DbSet<KnownVrcUser> KnownVrcUsers => Set<KnownVrcUser>();
    public DbSet<VrcUserAlias> VrcUserAliases => Set<VrcUserAlias>();

    private readonly string _dbPath;

    public VrcdnDbContext(string dbPath)
    {
        _dbPath = dbPath;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options) =>
        options.UseSqlite($"Data Source={_dbPath}");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Photo>(entity =>
        {
            entity.ToTable("photos");
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => p.LocalPath).IsUnique();
            entity.HasIndex(p => p.RemoteStatus);

            entity.Property(p => p.Id).HasColumnName("id");
            entity.Property(p => p.LocalPath).HasColumnName("local_path").IsRequired();
            entity.Property(p => p.FileSize).HasColumnName("file_size");
            entity.Property(p => p.Mtime).HasColumnName("mtime");
            entity.Property(p => p.Width).HasColumnName("width");
            entity.Property(p => p.Height).HasColumnName("height");
            entity.Property(p => p.FileHash).HasColumnName("file_hash");
            entity.Property(p => p.Thumbnail).HasColumnName("thumbnail");
            entity.Property(p => p.Rating).HasColumnName("rating");
            entity.Property(p => p.AvatarType).HasColumnName("avatar_type");
            entity.Property(p => p.AvatarTypeConfidence).HasColumnName("avatar_type_confidence");
            entity.Property(p => p.MetadataScanned).HasColumnName("metadata_scanned");
            entity.Property(p => p.AuthorId).HasColumnName("author_id");
            entity.Property(p => p.AuthorDisplayName).HasColumnName("author_display_name");
            entity.Property(p => p.WorldName).HasColumnName("world_name");
            entity.Property(p => p.PlayerNames).HasColumnName("player_names");
            entity.Property(p => p.Selected).HasColumnName("selected");
            entity.Property(p => p.RemoteStatus).HasColumnName("remote_status").HasConversion<string>();
            entity.Property(p => p.RemoteUrl).HasColumnName("remote_url");
            entity.Property(p => p.RemoteId).HasColumnName("remote_id");
            entity.Property(p => p.UploadedAt).HasColumnName("uploaded_at");
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
