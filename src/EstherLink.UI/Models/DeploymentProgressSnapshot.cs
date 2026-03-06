namespace EstherLink.UI.Models;

public static class DeploymentPhases
{
    public const string Relay = "relay";
    public const string GatewayBootstrap = "gateway_bootstrap";
    public const string GatewayInstall = "gateway_install";
    public const string GatewayHealth = "gateway_health";
    public const string GatewayCommand = "gateway_command";
}

public sealed class DeploymentProgressSnapshot
{
    public string Phase { get; set; } = DeploymentPhases.Relay;
    public int Percent { get; set; }
    public string Message { get; set; } = string.Empty;
}
