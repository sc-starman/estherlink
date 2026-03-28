using OmniRelay.Backend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;

namespace OmniRelay.Backend.Security;

public static class AdminAuthorizationPolicy
{
    public const string Name = "AdminUserOnly";

    public static void Configure(AuthorizationOptions options)
    {
        options.AddPolicy(Name, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.Requirements.Add(new IsAdminRequirement());
        });
    }
}

public sealed class IsAdminRequirement : IAuthorizationRequirement;

public sealed class IsAdminAuthorizationHandler : AuthorizationHandler<IsAdminRequirement>
{
    private readonly UserManager<ApplicationUser> _userManager;

    public IsAdminAuthorizationHandler(UserManager<ApplicationUser> userManager)
    {
        _userManager = userManager;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        IsAdminRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var user = await _userManager.GetUserAsync(context.User);
        if (user?.IsAdmin == true)
        {
            context.Succeed(requirement);
        }
    }
}
