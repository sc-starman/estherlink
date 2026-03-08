using OmniRelay.UI.Models;

namespace OmniRelay.UI.Services;

public interface IUiSettingsService
{
    event EventHandler<UiSettingsModel>? SettingsChanged;
    UiSettingsModel Load();
    void Save(UiSettingsModel settings);
}
