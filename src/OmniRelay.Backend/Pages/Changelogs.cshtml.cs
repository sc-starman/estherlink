using OmniRelay.Backend.Localization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace OmniRelay.Backend.Pages;

public sealed class ChangelogsModel : PageModel
{
    private readonly IStringLocalizer<SharedResource> _localizer;

    public ChangelogsModel(IStringLocalizer<SharedResource> localizer)
    {
        _localizer = localizer;
    }

    public IReadOnlyList<ChangelogEntry> ReleasedChangelogEntries =>
    [
        new("1.45.1", new DateTime(2026, 4, 25), [_localizer["Changelog.Release.1.45.1.1"], _localizer["Changelog.Release.1.45.1.2"]]),
        new("1.44.4", new DateTime(2026, 4, 22), [_localizer["Changelog.Release.1.44.4.1"], _localizer["Changelog.Release.1.44.4.2"]]),
        new("1.40.9", new DateTime(2026, 4, 14), [_localizer["Changelog.Release.1.40.9.1"], _localizer["Changelog.Release.1.40.9.2"]]),
        new("1.40.8", new DateTime(2026, 4, 12), [_localizer["Changelog.Release.1.40.8.1"], _localizer["Changelog.Release.1.40.8.2"]]),
        new("1.39.17", new DateTime(2026, 4, 11), [_localizer["Changelog.Release.1.39.17.1"], _localizer["Changelog.Release.1.39.17.2"]]),
        new("1.39.11", new DateTime(2026, 4, 9), [_localizer["Changelog.Release.1.39.11.1"], _localizer["Changelog.Release.1.39.11.2"]]),
        new("1.39.9", new DateTime(2026, 4, 4), [_localizer["Changelog.Release.1.39.9.1"], _localizer["Changelog.Release.1.39.9.2"]]),
        new("1.39.8", new DateTime(2026, 4, 3), [_localizer["Changelog.Release.1.39.8.1"], _localizer["Changelog.Release.1.39.8.2"]]),
        new("1.39.7", new DateTime(2026, 3, 31), [_localizer["Changelog.Release.1.39.7.1"], _localizer["Changelog.Release.1.39.7.2"]]),
        new("1.39.6", new DateTime(2026, 3, 29), [_localizer["Changelog.Release.1.39.6.1"], _localizer["Changelog.Release.1.39.6.2"]]),
        new("1.38.1", new DateTime(2026, 3, 28), [_localizer["Changelog.Release.1.38.1.1"], _localizer["Changelog.Release.1.38.1.2"]]),
        new("1.37.13", new DateTime(2026, 3, 22), [_localizer["Changelog.Release.1.37.13.1"], _localizer["Changelog.Release.1.37.13.2"]]),
        new("1.37.5", new DateTime(2026, 3, 16), [_localizer["Changelog.Release.1.37.5.1"], _localizer["Changelog.Release.1.37.5.2"]]),
        new("1.37.0", new DateTime(2026, 3, 13), [_localizer["Changelog.Release.1.37.0.1"], _localizer["Changelog.Release.1.37.0.2"]]),
        new("1.36.1", new DateTime(2026, 3, 13), [_localizer["Changelog.Release.1.36.1.1"], _localizer["Changelog.Release.1.36.1.2"]]),
        new("1.34.18", new DateTime(2026, 3, 10), [_localizer["Changelog.Release.1.34.18.1"], _localizer["Changelog.Release.1.34.18.2"]]),
        new("1.34.16", new DateTime(2026, 3, 9), [_localizer["Changelog.Release.1.34.16.1"], _localizer["Changelog.Release.1.34.16.2"]])
    ];

    public ChangelogEntry CurrentChangesEntry =>
        new(
            "2.1.19",
            DateTime.UtcNow.Date,
            [
                _localizer["Changelog.Current.2.1.19.1"],
                _localizer["Changelog.Current.2.1.19.2"],
                _localizer["Changelog.Current.2.1.19.3"],
                _localizer["Changelog.Current.2.1.19.4"],
                _localizer["Changelog.Current.2.1.19.5"]
            ]);

    public void OnGet()
    {
    }
}

public sealed record ChangelogEntry(string Version, DateTime PublishedOnUtc, IReadOnlyList<string> Highlights);
