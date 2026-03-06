using EstherLink.UI.Models;

namespace EstherLink.UI.Services;

public interface IGatewayStatePersistenceService
{
    GatewayUiStateModel Load();
    void Save(GatewayUiStateModel state);
}
