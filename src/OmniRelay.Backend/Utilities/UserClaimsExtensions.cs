using System.Security.Claims;

namespace OmniRelay.Backend.Utilities;

public static class UserClaimsExtensions
{
    public static Guid? GetUserId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var parsed) ? parsed : null;
    }
}