using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EstherLink.Backend.Pages.App;

[Authorize]
public sealed class IndexModel : PageModel
{
    public IActionResult OnGet()
    {
        return RedirectPermanentPreserveMethod("/dashboard");
    }
}
