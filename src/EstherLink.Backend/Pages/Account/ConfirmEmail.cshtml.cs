using System.Text;
using EstherLink.Backend.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;

namespace EstherLink.Backend.Pages.Account;

public sealed class ConfirmEmailModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;

    public ConfirmEmailModel(UserManager<ApplicationUser> userManager)
    {
        _userManager = userManager;
    }

    public bool Confirmed { get; private set; }
    public string Message { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(Guid? userId, string? code)
    {
        if (userId is null || string.IsNullOrWhiteSpace(code))
        {
            Message = "Invalid confirmation link.";
            return Page();
        }

        var user = await _userManager.FindByIdAsync(userId.Value.ToString());
        if (user is null)
        {
            Message = "User not found.";
            return Page();
        }

        string decodedCode;
        try
        {
            var bytes = WebEncoders.Base64UrlDecode(code);
            decodedCode = Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            Message = "Invalid confirmation token.";
            return Page();
        }

        var result = await _userManager.ConfirmEmailAsync(user, decodedCode);
        if (result.Succeeded)
        {
            Confirmed = true;
            Message = "Your email has been confirmed. You can now log in.";
            return Page();
        }

        Message = "Email confirmation failed. The link may be expired or already used.";
        return Page();
    }
}
