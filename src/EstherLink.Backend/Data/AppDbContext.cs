using EstherLink.Backend.Data.Entities;
using EstherLink.Backend.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace EstherLink.Backend.Data;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<LicenseEntity> Licenses => Set<LicenseEntity>();
    public DbSet<LicenseActivationEntity> LicenseActivations => Set<LicenseActivationEntity>();
    public DbSet<WhitelistSetEntity> WhitelistSets => Set<WhitelistSetEntity>();
    public DbSet<WhitelistEntryEntity> WhitelistEntries => Set<WhitelistEntryEntity>();
    public DbSet<AppReleaseEntity> AppReleases => Set<AppReleaseEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("public");

        modelBuilder.Entity<LicenseEntity>(entity =>
        {
            entity.ToTable("licenses");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.LicenseKey).HasColumnName("license_key").HasMaxLength(128).IsRequired();
            entity.HasIndex(x => x.LicenseKey).IsUnique();
            entity.Property(x => x.Status)
                .HasColumnName("status")
                .HasConversion(
                    x => x.ToString().ToLowerInvariant(),
                    x => Enum.Parse<LicenseStatus>(x, true))
                .HasMaxLength(16)
                .IsRequired();
            entity.Property(x => x.Plan).HasColumnName("plan").HasMaxLength(64).IsRequired();
            entity.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            entity.Property(x => x.MaxDevices).HasColumnName("max_devices").IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        });

        modelBuilder.Entity<LicenseActivationEntity>(entity =>
        {
            entity.ToTable("license_activations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.LicenseId).HasColumnName("license_id").IsRequired();
            entity.Property(x => x.FingerprintHash).HasColumnName("fingerprint_hash").HasMaxLength(128).IsRequired();
            entity.Property(x => x.FirstSeenAt).HasColumnName("first_seen_at").IsRequired();
            entity.Property(x => x.LastSeenAt).HasColumnName("last_seen_at").IsRequired();
            entity.Property(x => x.IsBlocked).HasColumnName("is_blocked").IsRequired();
            entity.Property(x => x.MetaJson).HasColumnName("meta_json").HasColumnType("jsonb").IsRequired();
            entity.HasIndex(x => new { x.LicenseId, x.FingerprintHash }).IsUnique();
            entity.HasOne(x => x.License)
                .WithMany(x => x.Activations)
                .HasForeignKey(x => x.LicenseId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WhitelistSetEntity>(entity =>
        {
            entity.ToTable("whitelist_sets");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.SetGroupId).HasColumnName("set_group_id").IsRequired();
            entity.HasIndex(x => new { x.SetGroupId, x.Version }).IsUnique();
            entity.Property(x => x.CountryCode).HasColumnName("country_code").HasMaxLength(2).IsRequired();
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            entity.Property(x => x.Category).HasColumnName("category").HasMaxLength(120);
            entity.Property(x => x.Version).HasColumnName("version").IsRequired();
            entity.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(64).IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.HasIndex(x => new { x.CountryCode, x.Category });
        });

        modelBuilder.Entity<WhitelistEntryEntity>(entity =>
        {
            entity.ToTable("whitelist_entries");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.WhitelistSetId).HasColumnName("whitelist_set_id").IsRequired();
            entity.Property(x => x.Cidr).HasColumnName("cidr").HasMaxLength(64).IsRequired();
            entity.Property(x => x.Note).HasColumnName("note").HasMaxLength(512);
            entity.HasIndex(x => new { x.WhitelistSetId, x.Cidr }).IsUnique();
            entity.HasOne(x => x.WhitelistSet)
                .WithMany(x => x.Entries)
                .HasForeignKey(x => x.WhitelistSetId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppReleaseEntity>(entity =>
        {
            entity.ToTable("app_releases");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Channel).HasColumnName("channel").HasMaxLength(32).IsRequired();
            entity.Property(x => x.Version).HasColumnName("version").HasMaxLength(64).IsRequired();
            entity.Property(x => x.PublishedAt).HasColumnName("published_at").IsRequired();
            entity.Property(x => x.Notes).HasColumnName("notes").HasColumnType("text").IsRequired();
            entity.Property(x => x.DownloadUrl).HasColumnName("download_url").HasColumnType("text").IsRequired();
            entity.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(64).IsRequired();
            entity.Property(x => x.MinSupportedVersion).HasColumnName("min_supported_version").HasMaxLength(64).IsRequired();
            entity.HasIndex(x => new { x.Channel, x.Version }).IsUnique();
        });
    }
}

