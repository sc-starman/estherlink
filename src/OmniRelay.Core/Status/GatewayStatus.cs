namespace OmniRelay.Core.Status;

public sealed class GatewayStatus
{
    public string GatewayType { get; set; } = "remote";
    public string TunnelState { get; set; } = "Disconnected";
    public string HealthState { get; set; } = "Disconnected";
    public string? HealthReasonCode { get; set; }
    public int ConsecutiveFailures { get; set; }
    public int RecoveryTier { get; set; }
    public string? RecoveryAction { get; set; }
    public DateTimeOffset? LastLocalProbeUtc { get; set; }
    public DateTimeOffset? LastEndToEndProbeUtc { get; set; }
    public DateTimeOffset? LastHealthyUtc { get; set; }
    public DateTimeOffset? LastStatusUpdateUtc { get; set; }
    public IReadOnlyList<string> ResilienceEvents { get; set; } = [];
    public bool ServiceRunning { get; set; }
    public bool ProxyRunning { get; set; }
    public int ProxyListenPort { get; set; }
    public bool TunnelConnected { get; set; }
    public DateTimeOffset? TunnelLastConnectedAtUtc { get; set; }
    public int TunnelReconnectCount { get; set; }
    public string? TunnelLastError { get; set; }
    public bool BootstrapSocksListening { get; set; }
    public bool BootstrapSocksRemoteForwardActive { get; set; }
    public string? BootstrapSocksLastError { get; set; }
    public bool LicenseValid { get; set; }
    public bool LicenseFromCache { get; set; }
    public DateTimeOffset? LicenseCheckedAtUtc { get; set; }
    public DateTimeOffset? LicenseExpiresAtUtc { get; set; }
    public string? LicenseReason { get; set; }
    public string LicenseSource { get; set; } = "unknown";
    public bool LicenseTransferRequired { get; set; }
    public int LicenseTransferLimitPerRollingYear { get; set; }
    public int LicenseTransfersUsedInWindow { get; set; }
    public int LicenseTransfersRemainingInWindow { get; set; }
    public DateTimeOffset? LicenseTransferWindowStartAt { get; set; }
    public string? LicenseActiveDeviceHint { get; set; }
    public string? WhitelistAdapterIp { get; set; }
    public string? DefaultAdapterIp { get; set; }
    public int WhitelistCount { get; set; }
    public int BlacklistCount { get; set; }
    public string? LastError { get; set; }
    public string LocalGatewayState { get; set; } = "inactive";
    public string? LocalGatewayHealthReason { get; set; }
    public string LocalGatewayProtocol { get; set; } = "vless_local_tcp_plain";
    public int LocalGatewayPort { get; set; }
    public int LocalGatewayClientsCount { get; set; }
    public bool LocalGatewayAutoRecovered { get; set; }
}
