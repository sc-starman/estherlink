namespace OmniRelay.UI.Services;

public interface ITrayIconService : IDisposable
{
    bool AllowWindowClose { get; }
    void Initialize();
}
