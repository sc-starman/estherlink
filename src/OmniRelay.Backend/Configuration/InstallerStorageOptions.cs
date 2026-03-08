namespace OmniRelay.Backend.Configuration;

public sealed class InstallerStorageOptions
{
    public string RootPath { get; set; } = "data/installers";
    public int MaxUploadMb { get; set; } = 1024;
}
