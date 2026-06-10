using OmniRelay.Core.Configuration;
using OmniRelay.Core.Status;
using OmniRelay.Ipc;
using OmniRelay.UI.Models;
using OmniRelay.UI.Services;
using OmniRelay.UI.ViewModels;
using OmniRelay.UI.Views.Dialogs;

namespace OmniRelay.UI.Tests;

public class ViewModelBehaviorTests
{
    [Fact]
    public void GatewayDeploymentRequest_BuildCommonArgs_IncludesRelayIdWhenScoped()
    {
        var baseRequest = BuildValidGatewayRequest();
        var request = new GatewayDeploymentRequest
        {
            RelayId = "relay_A-1",
            Config = baseRequest.Config,
            BootstrapMode = baseRequest.BootstrapMode,
            SelectedGatewayProtocol = baseRequest.SelectedGatewayProtocol,
            GatewayPublicPort = baseRequest.GatewayPublicPort,
            GatewayPanelPort = baseRequest.GatewayPanelPort,
            GatewaySni = baseRequest.GatewaySni,
            GatewayTarget = baseRequest.GatewayTarget,
            GatewayDohEndpoints = baseRequest.GatewayDohEndpoints
        };

        var args = InvokeBuildCommonArgs(request);

        Assert.Contains("--relay-id", args);
        Assert.Contains("'relay_A-1'", args);
    }

    [Fact]
    public void GatewayDeploymentRequest_BuildCommonArgs_RequiresRelayId()
    {
        var args = InvokeBuildCommonArgs(BuildValidGatewayRequest());

        Assert.Contains("--relay-id", args);
    }

    [Fact]
    public void GatewayDeploymentRequest_ValidateRequest_RejectsUnsafeRelayId()
    {
        var request = new GatewayDeploymentRequest
        {
            RelayId = "bad/relay",
            Config = BuildValidGatewayRequest().Config,
            SelectedGatewayProtocol = GatewayProtocols.VlessTlsSingbox,
            GatewayPublicPort = 443,
            GatewayPanelPort = 2054,
            GatewaySni = "www.apple.com",
            GatewayTarget = "www.apple.com:443",
            GatewayDohEndpoints = "https://1.1.1.1/dns-query"
        };

        var ex = Assert.ThrowsAny<Exception>(() => InvokeValidateRequest(request));

        Assert.Contains("Relay id", ex.InnerException?.Message ?? ex.Message);
    }

    [Fact]
    public void RelaysViewModel_BuildGatewayDeploymentRequest_PropagatesRelayId()
    {
        var relay = new RelayConfig
        {
            Id = "relay42",
            Name = "Relay 42",
            RemoteGateway = new RemoteGatewayConfig
            {
                TunnelHost = "203.0.113.10",
                Protocol = GatewayProtocols.VlessTlsSingbox
            }
        };

        var request = InvokeRelayBuildGatewayDeploymentRequest(relay);

        Assert.Equal("relay42", request.RelayId);
    }

    [Fact]
    public void RelaysViewModel_BuildFrpProfileResolutionForHost_ExcludesCurrentRelayWhenResolvingProfile()
    {
        var relays = new List<RelayConfig>
        {
            new()
            {
                Id = "current",
                GatewayType = GatewayTypes.Remote,
                FrpProfilePortOverride = 7000,
                FrpProfileTokenOverride = "shared-token",
                RemoteGateway = new RemoteGatewayConfig
                {
                    TunnelHost = "vps.example.com",
                    TunnelRemotePort = 15010
                }
            },
            new()
            {
                Id = "other",
                GatewayType = GatewayTypes.Remote,
                FrpProfilePortOverride = 7000,
                FrpProfileTokenOverride = "shared-token",
                RemoteGateway = new RemoteGatewayConfig
                {
                    TunnelHost = "vps.example.com",
                    TunnelRemotePort = 15000
                }
            }
        };

        var result = InvokeBuildFrpProfileResolutionForHost(relays, " VPS.EXAMPLE.COM ", "current");

        Assert.NotNull(result);
        Assert.True(result!.Found);
        Assert.Equal(7000, result.FrpServerPort);
        Assert.Equal("shared-token", result.AuthToken);
    }

    private static GatewayDeploymentRequest BuildValidGatewayRequest()
    {
        return new GatewayDeploymentRequest
        {
            RelayId = "relay-default",
            Config = new ServiceConfig
            {
                TunnelHost = "203.0.113.10",
                TunnelUser = "omnirelay",
                TunnelSshPort = 22,
                TunnelRemotePort = 15000
            },
            SelectedGatewayProtocol = GatewayProtocols.VlessTlsSingbox,
            GatewayPublicPort = 443,
            GatewayPanelPort = 2054,
            GatewayDohEndpoints = "https://1.1.1.1/dns-query"
        };
    }

    private static string InvokeBuildCommonArgs(GatewayDeploymentRequest request)
    {
        var method = typeof(GatewayDeploymentService).GetMethod("BuildCommonArgs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, [request, false, null])!;
    }

    private static void InvokeValidateRequest(GatewayDeploymentRequest request)
    {
        var method = typeof(GatewayDeploymentService).GetMethod("ValidateRequest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        method!.Invoke(null, [request]);
    }

    private static GatewayDeploymentRequest InvokeRelayBuildGatewayDeploymentRequest(RelayConfig relay)
    {
        var method = typeof(RelaysViewModel).GetMethod("BuildGatewayDeploymentRequest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return (GatewayDeploymentRequest)method!.Invoke(null, [relay])!;
    }

    private static RelayEditDialog.FrpHostProfileResolution? InvokeBuildFrpProfileResolutionForHost(
        IEnumerable<RelayConfig> relays,
        string host,
        string currentRelayId)
    {
        var method = typeof(RelaysViewModel).GetMethod("BuildFrpProfileResolutionForHost", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return (RelayEditDialog.FrpHostProfileResolution?)method!.Invoke(null, [relays, host, currentRelayId]);
    }

    [Fact]
    public async Task BuildLocalGatewayClientConfig_ParsesOpenVpnBundle()
    {
        var state = new GatewayStateStore();
        var gatewayClient = new FakeGatewayClientService
        {
            ClientConfigToReturn = new LocalGatewayClientConfigResponse(
                "openvpn_bundle",
                "client\nproto tcp-client\n",
                "OpenVPN Client Bundle",
                "ovpn_user",
                "ovpn_pass",
                "client.ovpn",
                "client\nproto tcp-client\n")
        };

        var orchestrator = new GatewayOrchestratorService(state, gatewayClient, new FakeServiceControlService(), new FakeGatewayStatePersistenceService());
        var result = await orchestrator.BuildLocalGatewayClientConfigAsync("abc", "relay-test");

        Assert.True(result.Success);
        Assert.Equal("openvpn_bundle", result.Mode);
        Assert.Equal("OpenVPN Client Bundle", result.Title);
        Assert.Equal("ovpn_user", result.Username);
        Assert.Equal("ovpn_pass", result.Password);
        Assert.Equal("client.ovpn", result.OvpnFileName);
        Assert.Contains("proto tcp-client", result.OvpnContent);
    }

    private sealed class FakeGatewayClientService : IGatewayClientService
    {
        public int UpdateWhitelistCallCount { get; private set; }
        public TimeSpan GetStatusDelay { get; set; } = TimeSpan.Zero;
        public GatewayStatus StatusToReturn { get; set; } = new();
        public LocalGatewayClientConfigResponse ClientConfigToReturn { get; set; } =
            new("uri", "vless://example", "Config", string.Empty, string.Empty, string.Empty, string.Empty);

        public async Task<IpcResponse?> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            if (GetStatusDelay > TimeSpan.Zero)
            {
                await Task.Delay(GetStatusDelay, cancellationToken);
            }

            var payload = IpcJson.Serialize(new StatusResponse(StatusToReturn));
            return new IpcResponse(true, null, payload);
        }

        public Task<IpcResponse?> GetAppStatusAsync(CancellationToken cancellationToken = default)
        {
            var payload = IpcJson.Serialize(new AppStatusResponse(new AppStatus
            {
                ServiceRunning = true,
                ProxyRunning = StatusToReturn.ProxyRunning,
                LicenseValid = StatusToReturn.LicenseValid,
                LicenseCheckedAtUtc = StatusToReturn.LicenseCheckedAtUtc,
                LicenseExpiresAtUtc = StatusToReturn.LicenseExpiresAtUtc,
                Relays = []
            }));
            return Task.FromResult<IpcResponse?>(new IpcResponse(true, null, payload));
        }

        public Task<IpcResponse?> ListRelaysAsync(CancellationToken cancellationToken = default)
        {
            var payload = IpcJson.Serialize(new RelaysResponse([]));
            return Task.FromResult<IpcResponse?>(new IpcResponse(true, null, payload));
        }

        public Task<IpcResponse?> GetRelayAsync(string relayId, CancellationToken cancellationToken = default)
        {
            var payload = IpcJson.Serialize(new RelayResponse(new RelayConfig { Id = relayId, Name = "Relay" }));
            return Task.FromResult<IpcResponse?>(new IpcResponse(true, null, payload));
        }

        public Task<IpcResponse?> UpsertRelayAsync(RelayConfig relay, CancellationToken cancellationToken = default)
        {
            var payload = IpcJson.Serialize(new RelayResponse(relay));
            return Task.FromResult<IpcResponse?>(new IpcResponse(true, null, payload));
        }

        public Task<IpcResponse?> DeleteRelayAsync(string relayId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> SetRelayEnabledAsync(string relayId, bool enabled, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> SetConfigAsync(ServiceConfig config, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        {
            var payload = IpcJson.Serialize(new CapabilitiesResponse("1.0.0", [IpcCommands.SetLicenseKey]));
            return Task.FromResult<IpcResponse?>(new IpcResponse(true, null, payload));
        }

        public Task<IpcResponse?> SetLicenseKeyAsync(string licenseKey, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> RequestLicenseTransferAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> UpdateWhitelistAsync(IReadOnlyList<string> entries, CancellationToken cancellationToken = default)
        {
            UpdateWhitelistCallCount++;
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> GetPolicyListAsync(string listType, string? relayId = null, CancellationToken cancellationToken = default)
        {
            var payload = IpcJson.Serialize(new GetPolicyListResponse(listType, [], 0, 1, DateTimeOffset.UtcNow));
            return Task.FromResult<IpcResponse?>(new IpcResponse(true, null, payload));
        }

        public Task<IpcResponse?> BeginPolicyUpdateAsync(string listType, string mode, string? relayId = null, CancellationToken cancellationToken = default)
        {
            var payload = IpcJson.Serialize(new BeginPolicyUpdateResponse(Guid.NewGuid().ToString("N"), listType, mode, relayId));
            return Task.FromResult<IpcResponse?>(new IpcResponse(true, null, payload));
        }

        public Task<IpcResponse?> AppendPolicyEntriesAsync(string sessionId, IReadOnlyList<string> entries, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> CommitPolicyUpdateAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            var payload = IpcJson.Serialize(new CommitPolicyUpdateResponse("whitelist", "replace", 0, 0, 0, 0, 1, DateTimeOffset.UtcNow));
            return Task.FromResult<IpcResponse?>(new IpcResponse(true, null, payload));
        }

        public Task<IpcResponse?> CancelPolicyUpdateAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> ListRelayPolicyListsAsync(string? relayId = null, CancellationToken cancellationToken = default)
        {
            var payload = IpcJson.Serialize(new ListRelayPolicyListsResponse(relayId ?? string.Empty, [], 1, DateTimeOffset.UtcNow, 0, 0, 0, 0));
            return Task.FromResult<IpcResponse?>(new IpcResponse(true, null, payload));
        }

        public Task<IpcResponse?> GetRelayPolicyListAsync(string listId, string? relayId = null, CancellationToken cancellationToken = default)
        {
            var payload = IpcJson.Serialize(new GetRelayPolicyListResponse(listId, relayId ?? string.Empty, "list", "whitelist", 0, [], 0, 1, DateTimeOffset.UtcNow));
            return Task.FromResult<IpcResponse?>(new IpcResponse(true, null, payload));
        }

        public Task<IpcResponse?> CreateRelayPolicyListAsync(string label, string listType, string? relayId = null, int? priority = null, CancellationToken cancellationToken = default)
        {
            var payload = IpcJson.Serialize(new RelayPolicyListMutationResponse("list1", relayId ?? string.Empty, label, listType, priority ?? 0, 0, 1, DateTimeOffset.UtcNow));
            return Task.FromResult<IpcResponse?>(new IpcResponse(true, null, payload));
        }

        public Task<IpcResponse?> UpdateRelayPolicyListMetaAsync(string listId, string label, string listType, string? relayId = null, CancellationToken cancellationToken = default)
        {
            var payload = IpcJson.Serialize(new RelayPolicyListMutationResponse(listId, relayId ?? string.Empty, label, listType, 0, 0, 1, DateTimeOffset.UtcNow));
            return Task.FromResult<IpcResponse?>(new IpcResponse(true, null, payload));
        }

        public Task<IpcResponse?> ReorderRelayPolicyListsAsync(IReadOnlyList<string> orderedListIds, string? relayId = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> ReplaceRelayPolicyListEntriesAsync(string listId, IReadOnlyList<string> entries, string? relayId = null, CancellationToken cancellationToken = default)
        {
            var payload = IpcJson.Serialize(new ReplaceRelayPolicyListEntriesResponse(listId, entries.Count, 0, 0, entries.Count, 1, DateTimeOffset.UtcNow));
            return Task.FromResult<IpcResponse?>(new IpcResponse(true, null, payload));
        }

        public Task<IpcResponse?> DeleteRelayPolicyListAsync(string listId, string? relayId = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> VerifyLicenseAsync(CancellationToken cancellationToken = default)
        {
            var payload = IpcJson.Serialize(new VerifyLicenseResponse(true, DateTimeOffset.UtcNow.AddDays(30), false, null, "online"));
            return Task.FromResult<IpcResponse?>(new IpcResponse(true, null, payload));
        }

        public Task<IpcResponse?> StartProxyAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> StopProxyAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> TestTunnelConnectionAsync(ServiceConfig config, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> ApplyLocalGatewayConfigAsync(LocalGatewayConfig config, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> StartLocalGatewayAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> StopLocalGatewayAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> RestartLocalGatewayAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> GetLocalGatewayClientsAsync(string relayId, CancellationToken cancellationToken = default)
        {
            var payload = IpcJson.Serialize(new LocalGatewayClientsResponse(LocalGatewayProtocols.VlessTcpPlain, 443, []));
            return Task.FromResult<IpcResponse?>(new IpcResponse(true, null, payload));
        }

        public Task<IpcResponse?> AddLocalGatewayClientAsync(string email, string relayId, string? remark, CancellationToken cancellationToken = default)
        {
            var record = new LocalGatewayClientRecord(
                Guid.NewGuid().ToString("N"),
                email,
                true,
                remark ?? email,
                LocalGatewayProtocols.VlessTcpPlain,
                "ovpn_test",
                "secret",
                0,
                0,
                0,
                0,
                0,
                DateTimeOffset.UtcNow);
            return Task.FromResult<IpcResponse?>(new IpcResponse(true, null, IpcJson.Serialize(record)));
        }

        public Task<IpcResponse?> UpdateLocalGatewayClientAsync(LocalGatewayClientRecord client, string relayId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> DeleteLocalGatewayClientAsync(string clientId, string relayId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> BuildLocalGatewayClientConfigAsync(string clientId, string relayId, CancellationToken cancellationToken = default)
        {
            var payload = IpcJson.Serialize(ClientConfigToReturn);
            return Task.FromResult<IpcResponse?>(new IpcResponse(true, null, payload));
        }
    }

    private sealed class FakeServiceControlService : IServiceControlService
    {
        public TimeSpan QueryDelay { get; set; } = TimeSpan.Zero;
        public string ServiceState { get; set; } = "Stopped";

        public async Task<string> QueryServiceStateAsync(CancellationToken cancellationToken = default)
        {
            if (QueryDelay > TimeSpan.Zero)
            {
                await Task.Delay(QueryDelay, cancellationToken);
            }

            return ServiceState;
        }

        public Task<bool> InstallOrStartWindowsServiceAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<bool> StopWindowsServiceAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<bool> UninstallWindowsServiceAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }
    }

    private sealed class FakeUiSettingsService : IUiSettingsService
    {
        public UiSettingsModel Current { get; private set; } = new();
        public UiSettingsModel? LastSaved { get; private set; }

        public event EventHandler<UiSettingsModel>? SettingsChanged;

        public UiSettingsModel Load()
        {
            return Current;
        }

        public void Save(UiSettingsModel settings)
        {
            LastSaved = settings;
            Current = settings;
            SettingsChanged?.Invoke(this, settings);
        }
    }

    private sealed class FakeThemeService : IThemeService
    {
        public string CurrentTheme { get; private set; } = "Dark";
        public string? LastAppliedTheme { get; private set; }

        public event EventHandler<string>? ThemeChanged;

        public void ApplySavedTheme()
        {
        }

        public void ApplyTheme(string theme)
        {
            CurrentTheme = theme;
            LastAppliedTheme = theme;
            ThemeChanged?.Invoke(this, theme);
        }
    }

    private sealed class FakeGatewayStatePersistenceService : IGatewayStatePersistenceService
    {
        public GatewayUiStateModel Load() => new();

        public void Save(GatewayUiStateModel state)
        {
        }
    }
}
