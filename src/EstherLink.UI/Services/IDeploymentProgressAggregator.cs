using EstherLink.UI.Models;

namespace EstherLink.UI.Services;

public interface IDeploymentProgressAggregator
{
    int ToOverallPercent(DeploymentProgressSnapshot snapshot);
}
