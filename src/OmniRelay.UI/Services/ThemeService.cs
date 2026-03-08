using OmniRelay.UI.Models;
using System.Windows;

namespace OmniRelay.UI.Services;

public sealed class ThemeService : IThemeService
{
    private readonly IUiSettingsService _settingsService;
    private string _currentTheme = "Dark";

    public ThemeService(IUiSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public string CurrentTheme => _currentTheme;
    public event EventHandler<string>? ThemeChanged;

    public void ApplySavedTheme()
    {
        var settings = _settingsService.Load();
        ApplyTheme(settings.Theme);
    }

    public void ApplyTheme(string theme)
    {
        var normalized = string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase)
            ? "Light"
            : "Dark";

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(x =>
            x.Source is not null &&
            x.Source.OriginalString.StartsWith("Resources/Themes/Theme.", StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            dictionaries.Remove(existing);
        }

        var uri = new Uri($"Resources/Themes/Theme.{normalized}.xaml", UriKind.Relative);
        dictionaries.Add(new ResourceDictionary { Source = uri });

        _currentTheme = normalized;

        var settings = _settingsService.Load();
        settings.Theme = normalized;
        _settingsService.Save(settings);
        ThemeChanged?.Invoke(this, normalized);
    }
}
