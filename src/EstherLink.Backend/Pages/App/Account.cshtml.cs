using EstherLink.Backend.Configuration;
using EstherLink.Backend.Data;
using EstherLink.Backend.Models;
using EstherLink.Backend.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EstherLink.Backend.Pages.App;

[Authorize]
public sealed class AccountModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly AppDbContext _dbContext;
    private readonly IOptions<WebOptions> _webOptions;

    public AccountModel(UserManager<ApplicationUser> userManager, AppDbContext dbContext, IOptions<WebOptions> webOptions)
    {
        _userManager = userManager;
        _dbContext = dbContext;
        _webOptions = webOptions;
    }

    public string Email { get; private set; } = string.Empty;
    public string CreatedAtUtc { get; private set; } = string.Empty;
    public bool HasTrial { get; private set; }
    public int PaidLicenseCount { get; private set; }
    public string DocumentationUrl { get; private set; } = string.Empty;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        DocumentationUrl = _webOptions.Value.DocumentationUrl;

        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return;
        }

        Email = user.Email ?? string.Empty;
        CreatedAtUtc = user.CreatedAt.ToString("u");

        var userId = User.GetUserId();
        if (userId is null)
        {
            return;
        }

        HasTrial = await _dbContext.UserLicenses.AsNoTracking().AnyAsync(x => x.UserId == userId && x.Source == "trial", cancellationToken);
        PaidLicenseCount = await _dbContext.UserLicenses.AsNoTracking().CountAsync(x => x.UserId == userId && x.Source == "purchase", cancellationToken);
    }
}