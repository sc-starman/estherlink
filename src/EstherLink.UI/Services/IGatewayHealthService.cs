using EstherLink.UI.Models;

namespace EstherLink.UI.Services;

public interface IGatewayHealthService
{
    Task<GatewayHealthReport> GetHealthAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        IProgress<DeploymentProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default);
}
