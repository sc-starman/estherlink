using EstherLink.Backend.Configuration;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace EstherLink.Backend.Pages;

public sealed class IndexModel : PageModel
{
    private readonly IOptions<WebOptions> _webOptions;

    public IndexModel(IOptions<WebOptions> webOptions)
    {
        _webOptions = webOptions;
    }

    public string DocumentationUrl { get; private set; } = string.Empty;

    public void OnGet()
    {
        DocumentationUrl = _webOptions.Value.DocumentationUrl;
    }
}