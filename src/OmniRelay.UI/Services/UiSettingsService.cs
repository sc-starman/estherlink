using System.Text.Json;
using System.IO;
using OmniRelay.UI.Models;

namespace OmniRelay.UI.Services;

public sealed class UiSettingsService : IUiSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static string RootDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OmniRelay");

    private static string SettingsPath => Path.Combine(RootDirectory, "ui.settings.json");

    public event EventHandler<UiSettingsModel>? SettingsChanged;

    public UiSettingsModel Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new UiSettingsModel();
            }

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<UiSettingsModel>(json, JsonOptions) ?? new UiSettingsModel();
        }
        catch
        {
            return new UiSettingsModel();
        }
    }

    public void Save(UiSettingsModel settings)
    {
        Directory.CreateDirectory(RootDirectory);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json);
        SettingsChanged?.Invoke(this, settings);
    }
}
