namespace OmniRelay.UI.Services;

public interface IServiceControlService
{
    Task<string> QueryServiceStateAsync(CancellationToken cancellationToken = default);
    Task<bool> InstallOrStartWindowsServiceAsync(CancellationToken cancellationToken = default);
    Task<bool> StopWindowsServiceAsync(CancellationToken cancellationToken = default);
    Task<bool> UninstallWindowsServiceAsync(CancellationToken cancellationToken = default);
}
