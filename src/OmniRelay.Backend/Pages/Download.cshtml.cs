using OmniRelay.Backend.Services.Commerce;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace OmniRelay.Backend.Pages;

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
