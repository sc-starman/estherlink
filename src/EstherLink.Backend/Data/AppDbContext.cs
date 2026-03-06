using EstherLink.Backend.Data.Entities;
using EstherLink.Backend.Data.Enums;
using EstherLink.Backend.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace EstherLink.Backend.Data;

public sealed class AppDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<LicenseEntity> Licenses => Set<LicenseEntity>();
    public DbSet<LicenseActivationEntity> LicenseActivations => Set<LicenseActivationEntity>();
    public DbSet<LicenseTransferEntity> LicenseTransfers => Set<LicenseTransferEntity>();
    public DbSet<WhitelistSetEntity> WhitelistSets => Set<WhitelistSetEntity>();
    public DbSet<WhitelistEntryEntity> WhitelistEntries => Set<WhitelistEntryEntity>();
    public DbSet<AppReleaseEntity> AppReleases => Set<AppReleaseEntity>();
    public DbSet<SigningKeyEntity> SigningKeys => Set<SigningKeyEntity>();
    public DbSet<AdminApiKeyEntity> AdminApiKeys => Set<AdminApiKeyEntity>();
    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();
    public DbSet<UserLicenseEntity> UserLicenses => Set<UserLicenseEntity>();
    public DbSet<CommerceOrderEntity> CommerceOrders => Set<CommerceOrderEntity>();
    public DbSet<PayKryptIntentEntity> PayKryptIntents => Set<PayKryptIntentEntity>();
    public DbSet<PayKryptWebhookEventEntity> PayKryptWebhookEvents => Set<PayKryptWebhookEventEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("public");

        modelBuilder.Entity<ApplicationUser>(entity =>
        {
            entity.ToTable("app_users");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(x => x.Email).HasMaxLength(256);
            entity.Property(x => x.NormalizedEmail).HasMaxLength(256);
            entity.Property(x => x.UserName).HasMaxLength(256);
            entity.Property(x => x.NormalizedUserName).HasMaxLength(256);
        });

        modelBuilder.Entity<IdentityRole<Guid>>().ToTable("app_roles");
        modelBuilder.Entity<IdentityUserRole<Guid>>().ToTable("app_user_roles");
        modelBuilder.Entity<IdentityUserClaim<Guid>>().ToTable("app_user_claims");
        modelBuilder.Entity<IdentityUserLogin<Guid>>().ToTable("app_user_logins");
        modelBuilder.Entity<IdentityRoleClaim<Guid>>().ToTable("app_role_claims");
        modelBuilder.Entity<IdentityUserToken<Guid>>().ToTable("app_user_tokens");

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

        modelBuilder.Entity<LicenseTransferEntity>(entity =>
        {
            entity.ToTable("license_transfers");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.LicenseId).HasColumnName("license_id").IsRequired();
            entity.Property(x => x.FromFingerprintHash).HasColumnName("from_fingerprint_hash").HasMaxLength(128);
            entity.Property(x => x.ToFingerprintHash).HasColumnName("to_fingerprint_hash").HasMaxLength(128).IsRequired();
            entity.Property(x => x.RequestId).HasColumnName("request_id").HasMaxLength(128).IsRequired();
            entity.Property(x => x.AppVersion).HasColumnName("app_version").HasMaxLength(64).IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(x => x.MetaJson).HasColumnName("meta_json").HasColumnType("jsonb").IsRequired();
            entity.HasIndex(x => new { x.LicenseId, x.CreatedAt });
            entity.HasOne(x => x.License)
                .WithMany(x => x.Transfers)
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

        modelBuilder.Entity<SigningKeyEntity>(entity =>
        {
            entity.ToTable("signing_keys");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.KeyId).HasColumnName("key_id").HasMaxLength(64).IsRequired();
            entity.Property(x => x.PublicKey).HasColumnName("public_key").HasColumnType("text").IsRequired();
            entity.Property(x => x.PrivateKey).HasColumnName("private_key").HasColumnType("text").IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            entity.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            entity.HasIndex(x => x.KeyId).IsUnique();
        });

        modelBuilder.Entity<AdminApiKeyEntity>(entity =>
        {
            entity.ToTable("admin_api_keys");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(128).IsRequired();
            entity.Property(x => x.KeyHash).HasColumnName("key_hash").HasMaxLength(128).IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            entity.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            entity.HasIndex(x => x.KeyHash).IsUnique();
        });

        modelBuilder.Entity<AuditEventEntity>(entity =>
        {
            entity.ToTable("audit_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Actor).HasColumnName("actor").HasMaxLength(128).IsRequired();
            entity.Property(x => x.Method).HasColumnName("method").HasMaxLength(16).IsRequired();
            entity.Property(x => x.Path).HasColumnName("path").HasMaxLength(512).IsRequired();
            entity.Property(x => x.PayloadHash).HasColumnName("payload_hash").HasMaxLength(128).IsRequired();
            entity.Property(x => x.StatusCode).HasColumnName("status_code").IsRequired();
            entity.Property(x => x.RequestId).HasColumnName("request_id").HasMaxLength(128).IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.HasIndex(x => x.CreatedAt);
            entity.HasIndex(x => x.Actor);
        });

        modelBuilder.Entity<UserLicenseEntity>(entity =>
        {
            entity.ToTable("user_licenses");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
            entity.Property(x => x.LicenseId).HasColumnName("license_id").IsRequired();
            entity.Property(x => x.Source).HasColumnName("source").HasMaxLength(24).IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(x => x.UpdatesEntitledUntil).HasColumnName("updates_entitled_until");

            entity.HasIndex(x => new { x.UserId, x.LicenseId }).IsUnique();
            entity.HasIndex(x => new { x.UserId, x.Source })
                .HasFilter("source = 'trial'")
                .IsUnique();

            entity.HasOne(x => x.User)
                .WithMany(x => x.UserLicenses)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.License)
                .WithMany(x => x.UserLicenses)
                .HasForeignKey(x => x.LicenseId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CommerceOrderEntity>(entity =>
        {
            entity.ToTable("commerce_orders");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
            entity.Property(x => x.OrderType).HasColumnName("order_type").HasMaxLength(32).IsRequired();
            entity.Property(x => x.FiatAmount).HasColumnName("fiat_amount").HasColumnType("numeric(18,2)").IsRequired();
            entity.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(8).IsRequired();
            entity.Property(x => x.Status).HasColumnName("status").HasMaxLength(32).IsRequired();
            entity.Property(x => x.IssuedLicenseId).HasColumnName("issued_license_id");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();

            entity.HasIndex(x => new { x.UserId, x.CreatedAt });
            entity.HasIndex(x => x.IssuedLicenseId).IsUnique();

            entity.HasOne(x => x.User)
                .WithMany(x => x.Orders)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.IssuedLicense)
                .WithMany(x => x.IssuedByOrders)
                .HasForeignKey(x => x.IssuedLicenseId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<PayKryptIntentEntity>(entity =>
        {
            entity.ToTable("paykrypt_intents");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.OrderId).HasColumnName("order_id").IsRequired();
            entity.Property(x => x.PayKryptIntentId).HasColumnName("paykrypt_intent_id").HasMaxLength(128).IsRequired();
            entity.Property(x => x.Status).HasColumnName("status").HasMaxLength(32).IsRequired();
            entity.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            entity.Property(x => x.RawJson).HasColumnName("raw_json").HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();

            entity.HasIndex(x => x.PayKryptIntentId).IsUnique();

            entity.HasOne(x => x.Order)
                .WithMany(x => x.PayKryptIntents)
                .HasForeignKey(x => x.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PayKryptWebhookEventEntity>(entity =>
        {
            entity.ToTable("paykrypt_webhook_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.EventId).HasColumnName("event_id").HasMaxLength(128).IsRequired();
            entity.Property(x => x.PayloadHash).HasColumnName("payload_hash").HasMaxLength(128).IsRequired();
            entity.Property(x => x.ReceivedAt).HasColumnName("received_at").IsRequired();
            entity.Property(x => x.ProcessedAt).HasColumnName("processed_at");
            entity.Property(x => x.Result).HasColumnName("result").HasMaxLength(32).IsRequired();
            entity.Property(x => x.RawJson).HasColumnName("raw_json").HasColumnType("jsonb").IsRequired();

            entity.HasIndex(x => x.EventId).IsUnique();
            entity.HasIndex(x => x.PayloadHash).IsUnique();
        });
    }
}
