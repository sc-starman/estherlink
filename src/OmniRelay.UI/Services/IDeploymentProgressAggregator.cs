using OmniRelay.UI.Models;

namespace OmniRelay.UI.Services;

public interface IDeploymentProgressAggregator
{
    int ToOverallPercent(DeploymentProgressSnapshot snapshot);
}
