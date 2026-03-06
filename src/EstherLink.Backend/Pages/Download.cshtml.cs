using EstherLink.Backend.Services.Commerce;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EstherLink.Backend.Pages;

public sealed class DownloadModel : PageModel
{
    private readonly IDownloadCatalogService _downloadCatalogService;

    public DownloadModel(IDownloadCatalogService downloadCatalogService)
    {
        _downloadCatalogService = downloadCatalogService;
    }

    public DownloadCatalogItem? WindowsRelease { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        WindowsRelease = await _downloadCatalogService.GetLatestAsync("stable", cancellationToken);
    }
}
