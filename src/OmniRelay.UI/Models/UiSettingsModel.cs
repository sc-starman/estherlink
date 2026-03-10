namespace OmniRelay.UI.Models;

public sealed class UiSettingsModel
{
    public string Theme { get; set; } = "Dark";
    public int RefreshIntervalSeconds { get; set; } = 5;
}
