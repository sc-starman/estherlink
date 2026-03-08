using Microsoft.AspNetCore.Identity;

namespace OmniRelay.Backend.Models;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<Data.Entities.UserLicenseEntity> UserLicenses { get; set; } = [];
    public ICollection<Data.Entities.CommerceOrderEntity> Orders { get; set; } = [];
}