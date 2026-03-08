using OmniRelay.UI.Models;

namespace OmniRelay.UI.Services;

public sealed class DeploymentProgressAggregator : IDeploymentProgressAggregator
{
    public int ToOverallPercent(DeploymentProgressSnapshot snapshot)
    {
        var pct = Math.Clamp(snapshot.Percent, 0, 100);

        return snapshot.Phase switch
        {
            DeploymentPhases.Relay => Math.Clamp((int)Math.Round(0.35 * pct, MidpointRounding.AwayFromZero), 0, 35),
            DeploymentPhases.GatewayBootstrap => Math.Clamp(35 + (int)Math.Round(0.30 * pct, MidpointRounding.AwayFromZero), 35, 65),
            DeploymentPhases.GatewayInstall => Math.Clamp(65 + (int)Math.Round(0.25 * pct, MidpointRounding.AwayFromZero), 65, 90),
            DeploymentPhases.GatewayHealth => Math.Clamp(90 + (int)Math.Round(0.10 * pct, MidpointRounding.AwayFromZero), 90, 100),
            _ => pct
        };
    }
}
