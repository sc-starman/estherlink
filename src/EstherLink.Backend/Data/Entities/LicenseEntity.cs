using EstherLink.Backend.Data.Enums;

namespace EstherLink.Backend.Data.Entities;

public sealed class LicenseEntity
{
    public Guid Id { get; set; }
    public string LicenseKey { get; set; } = string.Empty;
    public LicenseStatus Status { get; set; } = LicenseStatus.Active;
    public string Plan { get; set; } = "standard";
    public DateTimeOffset? ExpiresAt { get; set; }
    public int MaxDevices { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<LicenseActivationEntity> Activations { get; set; } = [];
    public ICollection<UserLicenseEntity> UserLicenses { get; set; } = [];
    public ICollection<CommerceOrderEntity> IssuedByOrders { get; set; } = [];
}
