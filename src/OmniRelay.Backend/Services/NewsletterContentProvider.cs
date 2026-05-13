using OmniRelay.Backend.Localization;
using Microsoft.Extensions.Localization;

namespace OmniRelay.Backend.Services;

public sealed class NewsletterContentProvider : INewsletterContentProvider
{
    private readonly IStringLocalizer<SharedResource> _localizer;

    public NewsletterContentProvider(IStringLocalizer<SharedResource> localizer)
    {
        _localizer = localizer;
    }

    public NewsletterSnapshot GetLatest()
    {
        var highlights = new List<string>
        {
            _localizer["Changelog.Current.2.1.24.2"],
            _localizer["Changelog.Current.2.1.24.5"],
            _localizer["Changelog.Current.2.1.24.6"],
            _localizer["Changelog.Current.2.1.24.12"],
            _localizer["Changelog.Current.2.1.24.14"],
            _localizer["Changelog.Current.2.1.24.20"],
            _localizer["Changelog.Current.2.1.24.21"],
            _localizer["Changelog.Current.2.1.24.22"]
        };

        return new NewsletterSnapshot(
            "2.1.24",
            _localizer["Changelog.Current.2.1.24.1"],
            highlights,
            "Download Free Trial Now");
    }
}
