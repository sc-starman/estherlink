using OmniRelay.UI.Models;

namespace OmniRelay.UI.Services;

public interface IGatewayStatePersistenceService
{
    GatewayUiStateModel Load();
    void Save(GatewayUiStateModel state);
}
