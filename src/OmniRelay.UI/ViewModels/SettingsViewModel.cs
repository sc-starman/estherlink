using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmniRelay.UI.Models;
using OmniRelay.UI.Services;

namespace OmniRelay.UI.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IUiSettingsService _settingsService;
    private readonly IThemeService _themeService;

    public SettingsViewModel(IUiSettingsService settingsService, IThemeService themeService)
    {
        _settingsService = settingsService;
        _themeService = themeService;
        LoadFromSettings();
    }

    [ObservableProperty]
    private bool darkThemeEnabled;

    [ObservableProperty]
    private int refreshIntervalSeconds = 5;

    [ObservableProperty]
    private bool compactMode;

    [ObservableProperty]
    private string feedback = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    private bool CanSave() => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        ResetDefaultsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (IsBusy)
        {
            return;
        }

        if (RefreshIntervalSeconds <= 0)
        {
            Feedback = "Refresh interval must be greater than zero.";
            return;
        }

        try
        {
            IsBusy = true;
            var settings = new UiSettingsModel
            {
                Theme = DarkThemeEnabled ? "Dark" : "Light",
                RefreshIntervalSeconds = RefreshIntervalSeconds,
                CompactMode = CompactMode
            };

            _settingsService.Save(settings);
            _themeService.ApplyTheme(settings.Theme);
            Feedback = "Settings saved.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void ResetDefaults()
    {
        DarkThemeEnabled = true;
        RefreshIntervalSeconds = 5;
        CompactMode = false;
        Save();
    }

    private void LoadFromSettings()
    {
        var settings = _settingsService.Load();
        DarkThemeEnabled = !string.Equals(settings.Theme, "Light", StringComparison.OrdinalIgnoreCase);
        RefreshIntervalSeconds = settings.RefreshIntervalSeconds <= 0 ? 5 : settings.RefreshIntervalSeconds;
        CompactMode = settings.CompactMode;
    }
}
