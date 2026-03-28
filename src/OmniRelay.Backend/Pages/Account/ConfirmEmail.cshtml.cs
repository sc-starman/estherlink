using System.Text;
using OmniRelay.Backend.Localization;
using OmniRelay.Backend.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Localization;

namespace OmniRelay.Backend.Pages.Account;

public sealed class ConfirmEmailModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public ConfirmEmailModel(
        UserManager<ApplicationUser> userManager,
        IStringLocalizer<SharedResource> localizer)
    {
        _userManager = userManager;
        _localizer = localizer;
    }

    public bool Confirmed { get; private set; }
    public string Message { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(Guid? userId, string? code)
    {
        if (userId is null || string.IsNullOrWhiteSpace(code))
        {
            Message = _localizer["ConfirmEmail.Msg.InvalidLink"];
            return Page();
        }

        var user = await _userManager.FindByIdAsync(userId.Value.ToString());
        if (user is null)
        {
            Message = _localizer["ConfirmEmail.Msg.UserNotFound"];
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
            Message = _localizer["ConfirmEmail.Msg.InvalidToken"];
            return Page();
        }

        var result = await _userManager.ConfirmEmailAsync(user, decodedCode);
        if (result.Succeeded)
        {
            Confirmed = true;
            Message = _localizer["ConfirmEmail.Msg.Success"];
            return Page();
        }

        Message = _localizer["ConfirmEmail.Msg.Failed"];
        return Page();
    }
}
