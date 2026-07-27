using Microsoft.EntityFrameworkCore;
using VrcdnManager.Models;

namespace VrcdnManager.Data;

public class VrcdnDbContext : DbContext
{
    public DbSet<Photo> Photos => Set<Photo>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();

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
            entity.Property(p => p.MetadataScanned).HasColumnName("metadata_scanned");
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
    }
}
