namespace OmniRelay.UI.Services;

public interface IThemeService
{
    string CurrentTheme { get; }
    event EventHandler<string>? ThemeChanged;
    void ApplySavedTheme();
    void ApplyTheme(string theme);
}
