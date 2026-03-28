using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Http;

namespace OmniRelay.Backend.Pages.Account;

public sealed class AccessDeniedModel : PageModel
{
    public IActionResult OnGet()
    {
        return StatusCode(StatusCodes.Status403Forbidden);
    }
}
