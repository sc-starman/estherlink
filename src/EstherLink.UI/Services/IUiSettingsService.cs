using EstherLink.UI.Models;

namespace EstherLink.UI.Services;

public interface IUiSettingsService
{
    event EventHandler<UiSettingsModel>? SettingsChanged;
    UiSettingsModel Load();
    void Save(UiSettingsModel settings);
}
