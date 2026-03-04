namespace EstherLink.UI.Services;

public interface IServiceControlService
{
    Task<string> QueryServiceStateAsync(CancellationToken cancellationToken = default);
    Task<bool> InstallOrStartWindowsServiceAsync(string exePath, CancellationToken cancellationToken = default);
    Task<bool> StopWindowsServiceAsync(CancellationToken cancellationToken = default);
}
