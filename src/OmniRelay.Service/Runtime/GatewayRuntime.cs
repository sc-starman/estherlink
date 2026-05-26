using System.Net;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Microsoft.Data.Sqlite;
using OmniRelay.Core.Configuration;
using OmniRelay.Core.Licensing;
using OmniRelay.Core.Networking;
using OmniRelay.Core.Policy;
using OmniRelay.Core.Status;

namespace OmniRelay.Service.Runtime;

public sealed class GatewayRuntime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly object _sync = new();
    private readonly ConfigStore _configStore;
    private readonly PolicyStore _policyStore;
    private readonly FileLogWriter _log;

    private ServiceConfig _config;
    private IReadOnlyList<string> _whitelistEntries;
    private IReadOnlyList<string> _blacklistEntries;
    private PolicyAddressIndex _whitelistIndex;
    private PolicyAddressIndex _blacklistIndex;
    private long _policyRevision;
    private DateTimeOffset _policyUpdatedAtUtc;
    private bool _proxyRequested;
    private bool _licenseTransferRequested;
    private GatewayStatus _status;
    private readonly Dictionary<string, RelayStatus> _relayStatuses = new(StringComparer.Ordinal);
    private long _tunnelRestartRequestVersion;
    private bool _localGatewayRequested;
    private bool _localGatewayRestartRequested;
    private long _localGatewayRequestVersion;
    private readonly Dictionary<string, string> _relayLocalAccountingSyncSignatures = new(StringComparer.Ordinal);

    public GatewayRuntime(ConfigStore configStore, PolicyStore policyStore, FileLogWriter log)
    {
        _configStore = configStore;
        _policyStore = policyStore;
        _log = log;

        var persisted = _configStore.Load();
        _config = CloneConfig(persisted.Config);
        ConfigStore.EnsureRelayPorts(_config.Relays);
        _whitelistEntries = [];
        _blacklistEntries = [];
        _whitelistIndex = PolicyAddressIndex.Build([]);
        _blacklistIndex = PolicyAddressIndex.Build([]);
        _status = new GatewayStatus();
        SyncRelayStatusesLocked();
        _localGatewayRequested = _config.LocalGateway.RuntimeEnabled;

        var policySnapshot = _policyStore.Load();
        ApplyPolicySnapshotLocked(policySnapshot);

        _status = new GatewayStatus
        {
            ServiceRunning = true,
            ProxyRunning = false,
            ProxyListenPort = _config.LocalProxyListenPort,
            WhitelistCount = _whitelistEntries.Count,
            BlacklistCount = _blacklistEntries.Count,
            GatewayType = GatewayTypes.Normalize(_config.GatewayType),
            LocalGatewayState = "inactive",
            LocalGatewayProtocol = LocalGatewayProtocols.Normalize(_config.LocalGateway.Protocol),
            LocalGatewayPort = _config.LocalGateway.Port,
            LocalGatewayClientsCount = 0
        };

        // Keep relay data-plane active by default after service startup.
        // ProxyCoordinatorWorker will still stop listeners if license is invalid.
        _proxyRequested = true;
    }

    public ServiceConfig GetConfigSnapshot()
    {
        lock (_sync)
        {
            return CloneConfig(_config);
        }
    }

    public IReadOnlyList<RelayConfig> ListRelays()
    {
        lock (_sync)
        {
            return _config.Relays.Select(CloneRelayConfig).ToList();
        }
    }

    public bool TryGetRelay(string relayId, out RelayConfig relay)
    {
        lock (_sync)
        {
            var found = _config.Relays.FirstOrDefault(x => string.Equals(x.Id, relayId, StringComparison.Ordinal));
            relay = found is null ? new RelayConfig() : CloneRelayConfig(found);
            return found is not null;
        }
    }

    public RelayConfig UpsertRelay(RelayConfig relay)
    {
        lock (_sync)
        {
            var normalized = CloneRelayConfig(relay);
            if (string.IsNullOrWhiteSpace(normalized.Id))
            {
                normalized.Id = Guid.NewGuid().ToString("N");
            }

            // Preserve existing relay port assignments. Resolve any collisions by adjusting
            // the upserted relay (placed last), not by mutating already-deployed relays.
            var candidateRelays = _config.Relays
                .Where(x => !string.Equals(x.Id, normalized.Id, StringComparison.Ordinal))
                .Select(CloneRelayConfig)
                .ToList();
            candidateRelays.Add(normalized);
            ValidateRelayPortIsolationOrThrow(candidateRelays);
            ConfigStore.EnsureRelayPorts(candidateRelays);
            normalized = candidateRelays.First(x => string.Equals(x.Id, normalized.Id, StringComparison.Ordinal));
            var index = _config.Relays.FindIndex(x => string.Equals(x.Id, normalized.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                _config.Relays[index] = normalized;
            }
            else
            {
                _config.Relays.Add(normalized);
            }

            ApplyFrpProfileOverridesForRelayLocked(normalized);
            ValidateFrpProfilesForHostConsistencyOrThrow(_config);

            _relayStatuses[normalized.Id] = BuildRelayStatus(normalized, null);
            _relayStatuses[normalized.Id].TunnelState = normalized.Enabled ? "PendingRestart" : "Disabled";
            _relayStatuses[normalized.Id].HealthState = normalized.Enabled ? "Pending" : "Disabled";
            _relayStatuses[normalized.Id].HealthReasonCode = normalized.Enabled
                ? "Relay configuration saved; waiting for runtime reconciliation."
                : "Relay disabled.";
            PersistLocked();
            return CloneRelayConfig(normalized);
        }
    }

    private void ApplyFrpProfileOverridesForRelayLocked(RelayConfig relay)
    {
        if (!string.Equals(GatewayTypes.Normalize(relay.GatewayType), GatewayTypes.Remote, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var host = (relay.RemoteGateway?.TunnelHost ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            return;
        }

        var requestedPort = relay.FrpProfilePortOverride is > 0 and <= 65535 ? relay.FrpProfilePortOverride : 7000;
        var requestedToken = (relay.FrpProfileTokenOverride ?? string.Empty).Trim();

        _config.FrpServerProfiles ??= [];
        var profile = _config.FrpServerProfiles.FirstOrDefault(x =>
            x is not null &&
            string.Equals((x.TunnelHost ?? string.Empty).Trim(), host, StringComparison.OrdinalIgnoreCase));

        if (profile is null)
        {
            profile = new FrpServerProfile
            {
                TunnelHost = host,
                FrpServerPort = requestedPort,
                AuthToken = requestedToken
            };
            _config.FrpServerProfiles.Add(profile);
        }
        else
        {
            profile.FrpServerPort = requestedPort;
            if (!string.IsNullOrWhiteSpace(requestedToken))
            {
                profile.AuthToken = requestedToken;
            }
        }

        var effectivePort = profile.FrpServerPort is > 0 and <= 65535 ? profile.FrpServerPort : 7000;
        var effectiveToken = profile.AuthToken ?? string.Empty;

        foreach (var existingRelay in _config.Relays.Where(x =>
                     string.Equals(GatewayTypes.Normalize(x.GatewayType), GatewayTypes.Remote, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals((x.RemoteGateway?.TunnelHost ?? string.Empty).Trim(), host, StringComparison.OrdinalIgnoreCase)))
        {
            existingRelay.FrpProfilePortOverride = effectivePort;
            existingRelay.FrpProfileTokenOverride = effectiveToken;
        }
    }

    public bool DeleteRelay(string relayId)
    {
        lock (_sync)
        {
            var relay = _config.Relays.FirstOrDefault(x => string.Equals(x.Id, relayId, StringComparison.Ordinal));
            if (relay is null)
            {
                return false;
            }

            _config.Relays.RemoveAll(x => string.Equals(x.Id, relayId, StringComparison.Ordinal));
            _relayStatuses.Remove(relayId);
            _relayLocalAccountingSyncSignatures.Remove(relayId);
            CleanupDeletedRelayRuntimeBestEffort(relay);
            PersistLocked();
            DeleteRelayArtifactsBestEffort(relayId);

            return true;
        }
    }

    private void CleanupDeletedRelayRuntimeBestEffort(RelayConfig relay)
    {
        try
        {
            if (!string.Equals(GatewayTypes.Normalize(relay.GatewayType), GatewayTypes.Local, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var metadataPath = ServicePaths.GetRelayLocalGatewayConnectorMetadataPath(relay.Id);
            var openVpnConfigPath = ServicePaths.GetRelayLocalGatewayOpenVpnServerConfigPath(relay.Id);
            var localPort = relay.LocalGateway?.Port ?? 0;
            var omniPanelPort = relay.OmniPanel?.Port ?? 0;

            var command =
                "$k=0; " +
                "$meta='" + EscapePowerShellSingleQuoted(metadataPath) + "'; " +
                "$ovpn='" + EscapePowerShellSingleQuoted(openVpnConfigPath) + "'; " +
                "$ports=@(" + localPort + "," + omniPanelPort + ") | Where-Object { $_ -gt 0 } | Select-Object -Unique; " +
                "$allowed=@('node','connector-core','openvpn','sing-box','singbox'); " +
                "Get-CimInstance Win32_Process | " +
                "Where-Object { $_.CommandLine -and (($_.Name -ieq 'connector-core.exe' -and $_.CommandLine -like ('*' + $meta + '*')) -or ($_.Name -ieq 'openvpn.exe' -and $_.CommandLine -like ('*' + $ovpn + '*'))) } | " +
                "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue; if ($?) { $k++ } }; " +
                "foreach($p in $ports){ " +
                "  netstat -ano -p tcp | Select-String (':'+$p+'\\s+.*LISTENING\\s+\\d+$') | ForEach-Object { " +
                "    $m=[regex]::Match($_.Line,'\\s(\\d+)\\s*$'); " +
                "    if($m.Success){ " +
                "      $pid=[int]$m.Groups[1].Value; " +
                "      $pr=Get-Process -Id $pid -ErrorAction SilentlyContinue; " +
                "      if($null -ne $pr -and $allowed -contains $pr.ProcessName.ToLowerInvariant()){ Stop-Process -Id $pid -Force -ErrorAction SilentlyContinue; if($?) { $k++ } } " +
                "    } " +
                "  } " +
                "} " +
                "Write-Output $k";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-NoLogo");
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(command);

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                return;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(8000);

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                _log.Warn($"Relay delete runtime cleanup stderr for '{relay.Name}': {stderr.Trim()}");
            }

            if (int.TryParse((stdout ?? string.Empty).Trim(), out var killed) && killed > 0)
            {
                _log.Info($"Relay delete runtime cleanup killed {killed} process(es) for '{relay.Name}'.");
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Relay delete runtime cleanup failed for '{relay.Name}': {ex.Message}");
        }
    }

    private static string EscapePowerShellSingleQuoted(string value)
    {
        return (value ?? string.Empty).Replace("'", "''", StringComparison.Ordinal);
    }

    private void DeleteRelayArtifactsBestEffort(string relayId)
    {
        var relayDirectory = ServicePaths.GetRelayLocalGatewayDirectory(relayId);
        if (!Directory.Exists(relayDirectory))
        {
            return;
        }

        try
        {
            if (TryDeleteDirectoryWithRetries(relayDirectory, out var error))
            {
                _log.Info($"Deleted relay artifacts directory: {relayDirectory}");
                return;
            }

            _log.Warn($"Failed deleting relay artifacts for relay '{relayId}': {error}");
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed deleting relay artifacts for relay '{relayId}': {ex.Message}");
        }
    }

    private static bool TryDeleteDirectoryWithRetries(string directoryPath, out string error)
    {
        error = string.Empty;
        var retryDelaysMs = new[] { 120, 220, 350, 500, 800, 1200, 1600 };

        for (var attempt = 0; attempt <= retryDelaysMs.Length; attempt++)
        {
            try
            {
                if (!Directory.Exists(directoryPath))
                {
                    return true;
                }

                // Best-effort attribute normalization first.
                foreach (var file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var attr = File.GetAttributes(file);
                        if ((attr & FileAttributes.ReadOnly) != 0)
                        {
                            File.SetAttributes(file, attr & ~FileAttributes.ReadOnly);
                        }
                    }
                    catch
                    {
                    }
                }

                Directory.Delete(directoryPath, recursive: true);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                if (attempt >= retryDelaysMs.Length)
                {
                    break;
                }

                Thread.Sleep(retryDelaysMs[attempt]);
            }
        }

        return !Directory.Exists(directoryPath);
    }

    public bool SetRelayEnabled(string relayId, bool enabled)
    {
        lock (_sync)
        {
            var relay = _config.Relays.FirstOrDefault(x => string.Equals(x.Id, relayId, StringComparison.Ordinal));
            if (relay is null)
            {
                return false;
            }

            relay.Enabled = enabled;
            _relayStatuses[relay.Id] = BuildRelayStatus(relay, _relayStatuses.TryGetValue(relay.Id, out var existing) ? existing : null);
            PersistLocked();
            return true;
        }
    }

    public IReadOnlyList<LocalGatewayClient> GetLocalGatewayClientsSnapshot(string relayId)
    {
        lock (_sync)
        {
            if (!TryGetRelayLocalContextLocked(relayId, out var relay, out var protocol, out _))
            {
                return [];
            }

            var clients = LoadRelayLocalGatewayClientsFromDb(relay.Id, protocol);
            ApplyRelayLocalGatewayAccountingSnapshot(relay.Id, clients);
            return clients
                .Select(CloneLocalGatewayClient)
                .ToList();
        }
    }

    public bool TryAddLocalGatewayClient(string email, string? remark, string relayId, out LocalGatewayClient? client, out string? error)
    {
        lock (_sync)
        {
            if (!TryGetRelayLocalContextLocked(relayId, out var relay, out var protocol, out error))
            {
                client = null;
                return false;
            }

            var clients = LoadRelayLocalGatewayClientsFromDb(relay.Id, protocol);
            if (!TryCreateLocalGatewayClient(clients, protocol, email, remark, out client, out error))
            {
                return false;
            }

            PersistRelayLocalGatewayClientsToDb(relay.Id, clients, protocol);
            SetRelayLocalClientCountLocked(relay.Id, clients.Count);
            return true;
        }
    }

    public bool TryUpdateLocalGatewayClient(LocalGatewayClient client, string relayId, out string? error)
    {
        lock (_sync)
        {
            if (!TryGetRelayLocalContextLocked(relayId, out var relay, out var protocol, out error))
            {
                return false;
            }

            var id = (client.Id ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                error = "Client id is required.";
                return false;
            }

            var clients = LoadRelayLocalGatewayClientsFromDb(relay.Id, protocol);
            var existing = clients.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
            if (existing is null)
            {
                error = "Client not found.";
                return false;
            }

            var normalizedEmail = (client.Email ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedEmail))
            {
                error = "Client email is required.";
                return false;
            }

            var duplicate = clients.Any(x =>
                !string.Equals(x.Id, id, StringComparison.Ordinal) &&
                string.Equals(x.Email, normalizedEmail, StringComparison.OrdinalIgnoreCase));
            if (duplicate)
            {
                error = "Client email already exists.";
                return false;
            }

            existing.Email = normalizedEmail;
            existing.Enabled = client.Enabled;
            existing.Remark = string.IsNullOrWhiteSpace(client.Remark) ? normalizedEmail : client.Remark.Trim();
            existing.TotalGB = double.IsFinite(client.TotalGB) && client.TotalGB >= 0 ? client.TotalGB : 0;
            existing.ExpiryTime = client.ExpiryTime < 0 ? 0 : client.ExpiryTime;
            existing.SpeedLimitKbps = client.SpeedLimitKbps < 0 ? 0 : client.SpeedLimitKbps;
            if (!string.IsNullOrWhiteSpace(client.Secret))
            {
                existing.Secret = client.Secret.Trim();
            }
            if (string.IsNullOrWhiteSpace(existing.Username))
            {
                var existingUsernames = clients
                    .Where(x => !string.Equals(x.Id, id, StringComparison.Ordinal))
                    .Select(x => x.Username)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                existing.Username = CreateLocalGatewayUsername(existing.Email, existingUsernames);
            }

            PersistRelayLocalGatewayClientsToDb(relay.Id, clients, protocol);
            SetRelayLocalClientCountLocked(relay.Id, clients.Count);
            error = null;
            return true;
        }
    }

    public bool TryDeleteLocalGatewayClient(string clientId, string relayId, out string? error)
    {
        lock (_sync)
        {
            if (!TryGetRelayLocalContextLocked(relayId, out var relay, out var protocol, out error))
            {
                return false;
            }

            var id = (clientId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                error = "Client id is required.";
                return false;
            }

            var clients = LoadRelayLocalGatewayClientsFromDb(relay.Id, protocol);
            var removed = clients.RemoveAll(x => string.Equals(x.Id, id, StringComparison.Ordinal));
            if (removed <= 0)
            {
                error = "Client not found.";
                return false;
            }

            PersistRelayLocalGatewayClientsToDb(relay.Id, clients, protocol);
            SetRelayLocalClientCountLocked(relay.Id, clients.Count);
            error = null;
            return true;
        }
    }

    public bool TryBuildLocalGatewayClientConfig(string clientId, string relayId, out LocalGatewayClientConfigPayload payload, out string? error)
    {
        lock (_sync)
        {
            if (!TryGetRelayLocalContextLocked(relayId, out var relay, out var protocol, out error))
            {
                payload = LocalGatewayClientConfigPayload.Empty;
                return false;
            }

            var id = (clientId ?? string.Empty).Trim();
            var clients = LoadRelayLocalGatewayClientsFromDb(relay.Id, protocol);
            var client = clients.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
            if (client is null)
            {
                payload = LocalGatewayClientConfigPayload.Empty;
                error = "Client not found.";
                return false;
            }

            payload = BuildLocalGatewayClientConfigPayload(relay.LocalGateway, client, ResolveLocalGatewayHostLocked(relay), protocol, relay.Id, out error);
            return error is null;
        }
    }

    public IReadOnlyList<string> GetWhitelistEntriesSnapshot()
    {
        lock (_sync)
        {
            return _whitelistEntries.ToList();
        }
    }

    public IReadOnlyList<string> GetBlacklistEntriesSnapshot()
    {
        lock (_sync)
        {
            return _blacklistEntries.ToList();
        }
    }

    public PolicyListSnapshot GetPolicyListSnapshot(string listType)
    {
        return GetPolicyListSnapshot(listType, string.Empty);
    }

    public PolicyListSnapshot GetPolicyListSnapshot(string listType, string relayId)
    {
        if (!string.IsNullOrWhiteSpace(relayId))
        {
            return _policyStore.GetList(listType, relayId);
        }

        lock (_sync)
        {
            var normalized = PolicyListTypes.Normalize(listType);
            var entries = normalized == PolicyListTypes.Blacklist
                ? _blacklistEntries
                : _whitelistEntries;
            return new PolicyListSnapshot(
                normalized,
                entries.ToList(),
                entries.Count,
                _policyRevision,
                _policyUpdatedAtUtc);
        }
    }

    public PolicyStoreSnapshot GetRelayPolicySnapshot(string relayId)
    {
        return _policyStore.Load(relayId);
    }

    public RelayPolicySetSnapshot GetRelayPolicySetSnapshot(string relayId)
    {
        return _policyStore.LoadPolicySet(relayId);
    }

    public RelayPolicyList? GetRelayPolicyListSnapshot(string relayId, string listId)
    {
        return _policyStore.GetPolicyList(listId, relayId);
    }

    public RelayPolicyListSummary CreateRelayPolicyList(string relayId, string label, string listType, int? priority)
    {
        lock (_sync)
        {
            var summary = _policyStore.CreatePolicyList(relayId, label, listType, priority);
            UpdateRelayPolicyStatusLocked(relayId);
            PersistLocked();
            return summary;
        }
    }

    public RelayPolicyListSummary UpdateRelayPolicyListMeta(string relayId, string listId, string label, string listType)
    {
        lock (_sync)
        {
            var summary = _policyStore.UpdatePolicyListMeta(relayId, listId, label, listType);
            UpdateRelayPolicyStatusLocked(relayId);
            PersistLocked();
            return summary;
        }
    }

    public IReadOnlyList<RelayPolicyListSummary> ReorderRelayPolicyLists(string relayId, IReadOnlyList<string> orderedListIds)
    {
        lock (_sync)
        {
            var reordered = _policyStore.ReorderPolicyLists(relayId, orderedListIds);
            UpdateRelayPolicyStatusLocked(relayId);
            PersistLocked();
            return reordered;
        }
    }

    public RelayPolicyListCommitResult ReplaceRelayPolicyListEntries(string relayId, string listId, IReadOnlyList<string> entries)
    {
        lock (_sync)
        {
            var result = _policyStore.ReplacePolicyListEntries(relayId, listId, entries);
            UpdateRelayPolicyStatusLocked(relayId);
            PersistLocked();
            return result;
        }
    }

    public bool DeleteRelayPolicyList(string relayId, string listId)
    {
        lock (_sync)
        {
            var deleted = _policyStore.DeletePolicyList(relayId, listId);
            if (deleted)
            {
                UpdateRelayPolicyStatusLocked(relayId);
                PersistLocked();
            }

            return deleted;
        }
    }

    public PolicyAddressIndex GetRelayWhitelistIndex(string relayId)
    {
        var snapshot = _policyStore.LoadLegacyFlattened(relayId);
        return BuildIndex(snapshot.WhitelistEntries, PolicyListTypes.Whitelist);
    }

    public PolicyAddressIndex GetRelayBlacklistIndex(string relayId)
    {
        var snapshot = _policyStore.LoadLegacyFlattened(relayId);
        return BuildIndex(snapshot.BlacklistEntries, PolicyListTypes.Blacklist);
    }

    public PolicyListSnapshot GetLegacyPolicyListSnapshot(string listType)
    {
        lock (_sync)
        {
            var normalized = PolicyListTypes.Normalize(listType);
            var entries = normalized == PolicyListTypes.Blacklist
                ? _blacklistEntries
                : _whitelistEntries;
            return new PolicyListSnapshot(
                normalized,
                entries.ToList(),
                entries.Count,
                _policyRevision,
                _policyUpdatedAtUtc);
        }
    }

    public void SetConfig(ServiceConfig config)
    {
        lock (_sync)
        {
            var previous = CloneConfig(_config);
            _config = CloneConfig(config);
            ValidateRelayPortIsolationOrThrow(_config.Relays);
            ConfigStore.EnsureRelayPorts(_config.Relays);
            SyncRelayStatusesLocked();
            _config.GatewayType = GatewayTypes.Normalize(_config.GatewayType);
            _config.LocalGateway.Protocol = LocalGatewayProtocols.Normalize(_config.LocalGateway.Protocol);
            _status.ProxyListenPort = _config.LocalProxyListenPort;
            _status.GatewayType = _config.GatewayType;
            _status.LocalGatewayProtocol = _config.LocalGateway.Protocol;
            _status.LocalGatewayPort = _config.LocalGateway.Port;
            _status.LocalGatewayClientsCount = 0;
            _localGatewayRequested = _config.LocalGateway.RuntimeEnabled;

            if (string.Equals(_config.GatewayType, GatewayTypes.Remote, StringComparison.OrdinalIgnoreCase) &&
                RequiresTunnelRestart(previous, _config, out var changeSummary))
            {
                RequestTunnelRestartLocked($"Tunnel restart requested after config change: {changeSummary}.");
            }
            else if (!string.Equals(_config.GatewayType, GatewayTypes.Remote, StringComparison.OrdinalIgnoreCase))
            {
                _status.TunnelState = "LocalMode";
                _status.HealthState = "Healthy";
                _status.HealthReasonCode = null;
            }

            if (RequiresLocalGatewayRestart(previous, _config))
            {
                _localGatewayRestartRequested = true;
                _localGatewayRequestVersion++;
            }

            PersistLocked();
        }
    }

    public void SetLicenseKey(string licenseKey)
    {
        lock (_sync)
        {
            var normalized = (licenseKey ?? string.Empty).Trim();
            if (!string.Equals(_config.LicenseKey ?? string.Empty, normalized, StringComparison.Ordinal))
            {
                // Clear pending transfer only when operator actually changes the key.
                _licenseTransferRequested = false;
            }

            _config.LicenseKey = normalized;
            PersistLocked();
        }
    }

    public bool TryApplyLocalGatewayConfig(LocalGatewayConfig config, out string? error)
    {
        lock (_sync)
        {
            var protocol = LocalGatewayProtocols.Normalize(config.Protocol);
            if (!LocalGatewayProtocols.IsSupportedInV1(protocol))
            {
                error = "Local protocol is not supported in v1.";
                return false;
            }

            if (config.Port <= 0 || config.Port > 65535)
            {
                error = "Local gateway port is invalid.";
                return false;
            }

            var bindAddress = string.IsNullOrWhiteSpace(config.BindAddress) ? "0.0.0.0" : config.BindAddress.Trim();
            if (!string.Equals(bindAddress, "0.0.0.0", StringComparison.OrdinalIgnoreCase) &&
                !IPAddress.TryParse(bindAddress, out _))
            {
                error = "Bind address must be 0.0.0.0 or a valid IPv4/IPv6 address.";
                return false;
            }
            var remoteAddress = (config.RemoteAddress ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(remoteAddress))
            {
                var hostType = Uri.CheckHostName(remoteAddress);
                if (hostType == UriHostNameType.Unknown)
                {
                    error = "Remote address must be empty, a hostname, or an IP address.";
                    return false;
                }
            }

            _config.GatewayType = GatewayTypes.Local;
            _config.LocalGateway.Protocol = protocol;
            _config.LocalGateway.Port = config.Port;
            _config.LocalGateway.BindAddress = bindAddress;
            _config.LocalGateway.RemoteAddress = remoteAddress;
            _config.LocalGateway.Remark = string.IsNullOrWhiteSpace(config.Remark) ? "OmniRelay Local Gateway" : config.Remark.Trim();
            _config.LocalGateway.RuntimeEnabled = config.RuntimeEnabled;
            _localGatewayRequested = _config.LocalGateway.RuntimeEnabled;
            _localGatewayRestartRequested = true;
            _localGatewayRequestVersion++;
            _status.GatewayType = _config.GatewayType;
            _status.LocalGatewayProtocol = _config.LocalGateway.Protocol;
            _status.LocalGatewayPort = _config.LocalGateway.Port;
            _status.LocalGatewayClientsCount = 0;
            _status.LastStatusUpdateUtc = DateTimeOffset.UtcNow;
            PersistLocked();
            error = null;
            return true;
        }
    }

    public void RequestLicenseTransfer()
    {
        lock (_sync)
        {
            _licenseTransferRequested = true;
        }
    }

    public bool ConsumeLicenseTransferRequest()
    {
        lock (_sync)
        {
            var requested = _licenseTransferRequested;
            _licenseTransferRequested = false;
            return requested;
        }
    }

    public bool TryUpdateWhitelist(IEnumerable<string> entries, out string? error)
    {
        return TryApplyPolicyUpdate(
            PolicyListTypes.Whitelist,
            PolicyUpdateModes.Replace,
            entries.ToList(),
            out _,
            out error);
    }

    public bool TryApplyPolicyUpdate(
        string listType,
        string mode,
        IReadOnlyList<string> entries,
        out PolicyCommitResult? result,
        out string? error)
    {
        return TryApplyPolicyUpdate(listType, mode, entries, string.Empty, out result, out error);
    }

    public bool TryApplyPolicyUpdate(
        string listType,
        string mode,
        IReadOnlyList<string> entries,
        string relayId,
        out PolicyCommitResult? result,
        out string? error)
    {
        lock (_sync)
        {
            try
            {
                result = _policyStore.ApplyUpdate(listType, mode, entries, relayId);
                if (string.IsNullOrWhiteSpace(relayId))
                {
                    ApplyPolicySnapshotLocked(_policyStore.Load());
                }
                else if (_relayStatuses.TryGetValue(relayId, out var status))
                {
                    var snapshot = _policyStore.Load(relayId);
                    status.WhitelistCount = snapshot.WhitelistEntries.Count;
                    status.BlacklistCount = snapshot.BlacklistEntries.Count;
                }
                PersistLocked();
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                result = null;
                error = ex.Message;
                return false;
            }
        }
    }

    public bool ShouldUseWhitelistAdapter(IPAddress? destinationAddress)
    {
        var match = EvaluatePolicyDestination(destinationAddress);
        return match.Action == PolicyMatchAction.Whitelist;
    }

    public bool ShouldBlockDestination(IPAddress? destinationAddress)
    {
        var match = EvaluatePolicyDestination(destinationAddress);
        return match.Action == PolicyMatchAction.Blacklist;
    }

    public RelayPolicyMatchResult EvaluatePolicyDestination(IPAddress? destinationAddress)
    {
        var lists = BuildCompiledPolicyLists(_policyStore.LoadPolicySet(string.Empty));
        return RelayPolicyMatcher.Evaluate(lists, destinationAddress);
    }

    public RelayPolicyMatchResult EvaluateRelayPolicyDestination(string relayId, IPAddress? destinationAddress)
    {
        var lists = BuildCompiledPolicyLists(_policyStore.LoadPolicySet(relayId));
        return RelayPolicyMatcher.Evaluate(lists, destinationAddress);
    }

    public void RequestProxyStart()
    {
        lock (_sync)
        {
            _proxyRequested = true;
        }
    }

    public void RequestProxyStop()
    {
        lock (_sync)
        {
            _proxyRequested = false;
            _status.ProxyRunning = false;
        }
    }

    public void RequestLocalGatewayStart()
    {
        lock (_sync)
        {
            _localGatewayRequested = true;
            _config.LocalGateway.RuntimeEnabled = true;
            _localGatewayRestartRequested = false;
            _localGatewayRequestVersion++;
            PersistLocked();
        }
    }

    public void RequestLocalGatewayStop()
    {
        lock (_sync)
        {
            _localGatewayRequested = false;
            _config.LocalGateway.RuntimeEnabled = false;
            _localGatewayRestartRequested = false;
            _localGatewayRequestVersion++;
            PersistLocked();
        }
    }

    public void RequestLocalGatewayRestart()
    {
        lock (_sync)
        {
            _localGatewayRequested = true;
            _config.LocalGateway.RuntimeEnabled = true;
            _localGatewayRestartRequested = true;
            _localGatewayRequestVersion++;
            PersistLocked();
        }
    }

    public long GetLocalGatewayRequestVersion()
    {
        lock (_sync)
        {
            return _localGatewayRequestVersion;
        }
    }

    public bool IsLocalGatewayRequested()
    {
        lock (_sync)
        {
            return _localGatewayRequested;
        }
    }

    public bool ConsumeLocalGatewayRestartRequested()
    {
        lock (_sync)
        {
            var requested = _localGatewayRestartRequested;
            _localGatewayRestartRequested = false;
            return requested;
        }
    }

    public bool IsProxyRequested()
    {
        lock (_sync)
        {
            return _proxyRequested;
        }
    }

    public void SetProxyRunning(bool running, int port)
    {
        lock (_sync)
        {
            _status.ProxyRunning = running;
            _status.ProxyListenPort = port;
            _status.LastStatusUpdateUtc = DateTimeOffset.UtcNow;
        }
    }

    public void SetAdapterIps(string? whitelistAdapterIp, string? defaultAdapterIp)
    {
        lock (_sync)
        {
            _status.WhitelistAdapterIp = whitelistAdapterIp;
            _status.DefaultAdapterIp = defaultAdapterIp;
            _status.LastStatusUpdateUtc = DateTimeOffset.UtcNow;
        }
    }

    public void SetTunnelStatus(bool connected, DateTimeOffset? connectedAtUtc, int reconnectCount, string? error)
    {
        lock (_sync)
        {
            _status.TunnelConnected = connected;
            _status.TunnelLastConnectedAtUtc = connectedAtUtc;
            _status.TunnelReconnectCount = reconnectCount;
            _status.TunnelLastError = error;
            _status.TunnelState = connected ? "Healthy" : "Disconnected";
            _status.HealthState = connected ? "Healthy" : "Disconnected";
            _status.HealthReasonCode = error;
            _status.LastStatusUpdateUtc = DateTimeOffset.UtcNow;
        }
    }

    public void SetBootstrapSocksStatus(bool listening, bool remoteForwardActive, string? error)
    {
        lock (_sync)
        {
            _status.BootstrapSocksListening = listening;
            _status.BootstrapSocksRemoteForwardActive = remoteForwardActive;
            _status.BootstrapSocksLastError = error;
            _status.LastStatusUpdateUtc = DateTimeOffset.UtcNow;
        }
    }

    public void SetLocalGatewayStatus(string state, string? reason, bool autoRecovered)
    {
        lock (_sync)
        {
            _status.LocalGatewayState = string.IsNullOrWhiteSpace(state) ? "inactive" : state.Trim();
            _status.LocalGatewayHealthReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
            _status.LocalGatewayAutoRecovered = autoRecovered;
            _status.LocalGatewayProtocol = LocalGatewayProtocols.Normalize(_config.LocalGateway.Protocol);
            _status.LocalGatewayPort = _config.LocalGateway.Port;
            _status.LocalGatewayClientsCount = 0;
            _status.LastStatusUpdateUtc = DateTimeOffset.UtcNow;
        }
    }

    public void SetResilienceStatus(
        string tunnelState,
        string healthState,
        string? healthReasonCode,
        int consecutiveFailures,
        int recoveryTier,
        string? recoveryAction,
        DateTimeOffset? lastLocalProbeUtc,
        DateTimeOffset? lastEndToEndProbeUtc,
        DateTimeOffset? lastHealthyUtc,
        bool tunnelConnected,
        bool bootstrapSocksListening,
        bool bootstrapSocksRemoteForwardActive,
        string? tunnelLastError,
        string? bootstrapSocksLastError,
        int tunnelReconnectCount,
        DateTimeOffset? tunnelLastConnectedAtUtc,
        IReadOnlyList<string>? resilienceEvents)
    {
        lock (_sync)
        {
            _status.GatewayType = GatewayTypes.Normalize(_config.GatewayType);
            _status.TunnelState = tunnelState;
            _status.HealthState = healthState;
            _status.HealthReasonCode = healthReasonCode;
            _status.ConsecutiveFailures = Math.Max(0, consecutiveFailures);
            _status.RecoveryTier = Math.Max(0, recoveryTier);
            _status.RecoveryAction = recoveryAction;
            _status.LastLocalProbeUtc = lastLocalProbeUtc;
            _status.LastEndToEndProbeUtc = lastEndToEndProbeUtc;
            _status.LastHealthyUtc = lastHealthyUtc;
            _status.LastStatusUpdateUtc = DateTimeOffset.UtcNow;

            _status.TunnelConnected = tunnelConnected;
            _status.BootstrapSocksListening = bootstrapSocksListening;
            _status.BootstrapSocksRemoteForwardActive = bootstrapSocksRemoteForwardActive;
            _status.TunnelLastError = tunnelLastError;
            _status.BootstrapSocksLastError = bootstrapSocksLastError;
            _status.TunnelReconnectCount = tunnelReconnectCount;
            _status.TunnelLastConnectedAtUtc = tunnelLastConnectedAtUtc;
            _status.ResilienceEvents = resilienceEvents?.ToArray() ?? [];
        }
    }

    public void RequestTunnelRestart(string reason)
    {
        lock (_sync)
        {
            RequestTunnelRestartLocked(reason);
        }
    }

    public long GetTunnelRestartRequestVersion()
    {
        lock (_sync)
        {
            return _tunnelRestartRequestVersion;
        }
    }

    public void SetLicenseStatus(LicenseValidationResult result)
    {
        lock (_sync)
        {
            _status.LicenseValid = result.IsValid;
            _status.LicenseFromCache = result.FromCache;
            _status.LicenseCheckedAtUtc = result.CheckedAtUtc;
            _status.LicenseExpiresAtUtc = result.ExpiresAtUtc;
            _status.LicenseReason = result.Reason;
            _status.LicenseSource = string.IsNullOrWhiteSpace(result.Source) ? "unknown" : result.Source;
            _status.LicenseTransferRequired = result.TransferRequired;
            _status.LicenseTransferLimitPerRollingYear = result.TransferLimitPerRollingYear;
            _status.LicenseTransfersUsedInWindow = result.TransfersUsedInWindow;
            _status.LicenseTransfersRemainingInWindow = result.TransfersRemainingInWindow;
            _status.LicenseTransferWindowStartAt = result.TransferWindowStartAt;
            _status.LicenseActiveDeviceHint = result.ActiveDeviceIdHint;
            _status.LastError = result.Error;
            _status.LastStatusUpdateUtc = DateTimeOffset.UtcNow;
            if (!result.IsValid)
            {
                MarkRelayStatusesDisconnectedLocked(result.Reason ?? result.Error ?? "License invalid.");
            }
        }
    }

    public void SetError(string? message)
    {
        lock (_sync)
        {
            _status.LastError = message;
            _status.LastStatusUpdateUtc = DateTimeOffset.UtcNow;
        }
    }

    public GatewayStatus GetStatusSnapshot()
    {
        lock (_sync)
        {
            return new GatewayStatus
            {
                GatewayType = _status.GatewayType,
                TunnelState = _status.TunnelState,
                HealthState = _status.HealthState,
                HealthReasonCode = _status.HealthReasonCode,
                ConsecutiveFailures = _status.ConsecutiveFailures,
                RecoveryTier = _status.RecoveryTier,
                RecoveryAction = _status.RecoveryAction,
                LastLocalProbeUtc = _status.LastLocalProbeUtc,
                LastEndToEndProbeUtc = _status.LastEndToEndProbeUtc,
                LastHealthyUtc = _status.LastHealthyUtc,
                LastStatusUpdateUtc = _status.LastStatusUpdateUtc,
                ResilienceEvents = _status.ResilienceEvents?.ToArray() ?? [],
                ServiceRunning = _status.ServiceRunning,
                ProxyRunning = _status.ProxyRunning,
                ProxyListenPort = _status.ProxyListenPort,
                TunnelConnected = _status.TunnelConnected,
                TunnelLastConnectedAtUtc = _status.TunnelLastConnectedAtUtc,
                TunnelReconnectCount = _status.TunnelReconnectCount,
                TunnelLastError = _status.TunnelLastError,
                BootstrapSocksListening = _status.BootstrapSocksListening,
                BootstrapSocksRemoteForwardActive = _status.BootstrapSocksRemoteForwardActive,
                BootstrapSocksLastError = _status.BootstrapSocksLastError,
                LicenseValid = _status.LicenseValid,
                LicenseFromCache = _status.LicenseFromCache,
                LicenseCheckedAtUtc = _status.LicenseCheckedAtUtc,
                LicenseExpiresAtUtc = _status.LicenseExpiresAtUtc,
                LicenseReason = _status.LicenseReason,
                LicenseSource = _status.LicenseSource,
                LicenseTransferRequired = _status.LicenseTransferRequired,
                LicenseTransferLimitPerRollingYear = _status.LicenseTransferLimitPerRollingYear,
                LicenseTransfersUsedInWindow = _status.LicenseTransfersUsedInWindow,
                LicenseTransfersRemainingInWindow = _status.LicenseTransfersRemainingInWindow,
                LicenseTransferWindowStartAt = _status.LicenseTransferWindowStartAt,
                LicenseActiveDeviceHint = _status.LicenseActiveDeviceHint,
                WhitelistAdapterIp = _status.WhitelistAdapterIp,
                DefaultAdapterIp = _status.DefaultAdapterIp,
                WhitelistCount = _status.WhitelistCount,
                BlacklistCount = _status.BlacklistCount,
                LastError = _status.LastError,
                LocalGatewayState = _status.LocalGatewayState,
                LocalGatewayHealthReason = _status.LocalGatewayHealthReason,
                LocalGatewayProtocol = _status.LocalGatewayProtocol,
                LocalGatewayPort = _status.LocalGatewayPort,
                LocalGatewayClientsCount = _status.LocalGatewayClientsCount,
                LocalGatewayAutoRecovered = _status.LocalGatewayAutoRecovered
            };
        }
    }

    public AppStatus GetAppStatusSnapshot()
    {
        lock (_sync)
        {
            SyncRelayStatusesLocked();
            return new AppStatus
            {
                ServiceRunning = true,
                ProxyRunning = _relayStatuses.Values.Any(x => x.Enabled && x.DataPlaneListening),
                LicenseValid = _status.LicenseValid,
                LicenseFromCache = _status.LicenseFromCache,
                LicenseCheckedAtUtc = _status.LicenseCheckedAtUtc,
                LicenseExpiresAtUtc = _status.LicenseExpiresAtUtc,
                LicenseReason = _status.LicenseReason,
                LicenseSource = _status.LicenseSource,
                LicenseTransferRequired = _status.LicenseTransferRequired,
                LicenseTransferLimitPerRollingYear = _status.LicenseTransferLimitPerRollingYear,
                LicenseTransfersUsedInWindow = _status.LicenseTransfersUsedInWindow,
                LicenseTransfersRemainingInWindow = _status.LicenseTransfersRemainingInWindow,
                LicenseTransferWindowStartAt = _status.LicenseTransferWindowStartAt,
                LicenseActiveDeviceHint = _status.LicenseActiveDeviceHint,
                Relays = _relayStatuses.Values.Select(CloneRelayStatus).ToList()
            };
        }
    }

    public void SetRelayRuntimeStatus(RelayStatus status)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(status.RelayId))
            {
                return;
            }

            _relayStatuses[status.RelayId] = CloneRelayStatus(status);
            _status.ProxyRunning = _relayStatuses.Values.Any(x => x.Enabled && x.DataPlaneListening);
            _status.LastStatusUpdateUtc = DateTimeOffset.UtcNow;
        }
    }

    private void PersistLocked()
    {
        _configStore.Save(_config);
    }

    private void SyncRelayStatusesLocked()
    {
        var relayIds = _config.Relays.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var staleId in _relayStatuses.Keys.Where(x => !relayIds.Contains(x)).ToArray())
        {
            _relayStatuses.Remove(staleId);
        }

        foreach (var relay in _config.Relays)
        {
            _relayStatuses[relay.Id] = BuildRelayStatus(relay, _relayStatuses.TryGetValue(relay.Id, out var existing) ? existing : null);
        }
    }

    private RelayStatus BuildRelayStatus(RelayConfig relay, RelayStatus? existing)
    {
        var snapshot = _policyStore.Load(relay.Id);
        var status = existing is null ? new RelayStatus() : CloneRelayStatus(existing);
        status.RelayId = relay.Id;
        status.Name = relay.Name;
        status.Enabled = relay.Enabled;
        status.GatewayType = GatewayTypes.Normalize(relay.GatewayType);
        status.LocalGatewayProtocol = LocalGatewayProtocols.Normalize(relay.LocalGateway?.Protocol);
        status.LocalGatewayPort = relay.LocalGateway?.Port ?? 0;
        status.OmniPanelUrl = relay.OmniPanel?.PublicUrl;
        status.OmniPanelState = string.IsNullOrWhiteSpace(relay.OmniPanel?.Domain) ? "not_configured" : "configured";
        status.OmniPanelLastError = string.IsNullOrWhiteSpace(relay.OmniPanel?.LastError) ? null : relay.OmniPanel.LastError;
        status.WhitelistCount = snapshot.WhitelistEntries.Count;
        status.BlacklistCount = snapshot.BlacklistEntries.Count;
        if (_status.LicenseCheckedAtUtc is not null && !_status.LicenseValid)
        {
            var reason = _status.LicenseReason ?? _status.LastError ?? "License invalid.";
            status.TunnelState = "Disconnected";
            status.HealthState = "Disconnected";
            status.HealthReasonCode = "license_inactive";
            status.RecoveryAction = null;
            status.TunnelConnected = false;
            status.DataPlaneListening = false;
            status.BootstrapSocksListening = false;
            status.BootstrapSocksRemoteForwardActive = false;
            status.TunnelLastError = reason;
            status.BootstrapSocksLastError = reason;
            status.LastError = reason;
            status.LastStatusUpdateUtc = DateTimeOffset.UtcNow;
        }
        return status;
    }

    private void MarkRelayStatusesDisconnectedLocked(string reason)
    {
        foreach (var key in _relayStatuses.Keys.ToArray())
        {
            var status = _relayStatuses[key];
            status.TunnelState = "Disconnected";
            status.HealthState = "Disconnected";
            status.HealthReasonCode = "license_inactive";
            status.RecoveryAction = null;
            status.TunnelConnected = false;
            status.DataPlaneListening = false;
            status.BootstrapSocksListening = false;
            status.BootstrapSocksRemoteForwardActive = false;
            status.TunnelLastError = reason;
            status.BootstrapSocksLastError = reason;
            status.LastError = reason;
            status.LastStatusUpdateUtc = DateTimeOffset.UtcNow;
        }

        _status.ProxyRunning = false;
        _status.TunnelConnected = false;
        _status.BootstrapSocksListening = false;
        _status.BootstrapSocksRemoteForwardActive = false;
        _status.TunnelLastError = reason;
        _status.BootstrapSocksLastError = reason;
    }

    private static RelayStatus CloneRelayStatus(RelayStatus status)
    {
        return new RelayStatus
        {
            RelayId = status.RelayId,
            Name = status.Name,
            Enabled = status.Enabled,
            GatewayType = status.GatewayType,
            StatusStale = status.StatusStale,
            TunnelState = status.TunnelState,
            HealthState = status.HealthState,
            HealthReasonCode = status.HealthReasonCode,
            ConsecutiveFailures = status.ConsecutiveFailures,
            RecoveryTier = status.RecoveryTier,
            RecoveryAction = status.RecoveryAction,
            LastLocalProbeUtc = status.LastLocalProbeUtc,
            LastEndToEndProbeUtc = status.LastEndToEndProbeUtc,
            LastHealthyUtc = status.LastHealthyUtc,
            LastStatusUpdateUtc = status.LastStatusUpdateUtc,
            TunnelConnected = status.TunnelConnected,
            TunnelLastConnectedAtUtc = status.TunnelLastConnectedAtUtc,
            TunnelReconnectCount = status.TunnelReconnectCount,
            TunnelLastError = status.TunnelLastError,
            DataPlaneListening = status.DataPlaneListening,
            BootstrapSocksListening = status.BootstrapSocksListening,
            BootstrapSocksRemoteForwardActive = status.BootstrapSocksRemoteForwardActive,
            BootstrapSocksLastError = status.BootstrapSocksLastError,
            IncomingAdapterIp = status.IncomingAdapterIp,
            OutgoingAdapterIp = status.OutgoingAdapterIp,
            WhitelistCount = status.WhitelistCount,
            BlacklistCount = status.BlacklistCount,
            LastError = status.LastError,
            LocalGatewayState = status.LocalGatewayState,
            LocalGatewayHealthReason = status.LocalGatewayHealthReason,
            LocalGatewayProtocol = status.LocalGatewayProtocol,
            LocalGatewayPort = status.LocalGatewayPort,
            LocalGatewayClientsCount = status.LocalGatewayClientsCount,
            OmniPanelState = status.OmniPanelState,
            OmniPanelUrl = status.OmniPanelUrl,
            OmniPanelLastError = status.OmniPanelLastError
        };
    }

    private void RequestTunnelRestartLocked(string reason)
    {
        _tunnelRestartRequestVersion++;
        _status.TunnelConnected = false;
        _status.BootstrapSocksRemoteForwardActive = false;
        _status.TunnelLastError = reason;
        _status.BootstrapSocksLastError = reason;
        _status.TunnelState = "RecoveringTier1";
        _status.HealthState = "Degraded";
        _status.HealthReasonCode = reason;
        _status.LastStatusUpdateUtc = DateTimeOffset.UtcNow;
    }

    private static bool RequiresTunnelRestart(ServiceConfig previous, ServiceConfig current, out string summary)
    {
        var changed = new List<string>(8);

        if (previous.WhitelistAdapterIfIndex != current.WhitelistAdapterIfIndex)
        {
            changed.Add("IC1 adapter");
        }

        if (!string.Equals((previous.TunnelHost ?? string.Empty).Trim(), (current.TunnelHost ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
        {
            changed.Add("tunnel host");
        }

        if (previous.TunnelSshPort != current.TunnelSshPort)
        {
            changed.Add("SSH port");
        }

        if (previous.FrpServerPort != current.FrpServerPort)
        {
            changed.Add("FRP server port");
        }

        if (previous.TunnelRemotePort != current.TunnelRemotePort)
        {
            changed.Add("remote data port");
        }

        if (previous.TunnelRemotePort != current.TunnelRemotePort)
        {
            changed.Add("remote bootstrap port");
        }

        if (previous.LocalProxyListenPort != current.LocalProxyListenPort)
        {
            changed.Add("local proxy port");
        }

        if (previous.BootstrapSocksLocalPort != current.BootstrapSocksLocalPort)
        {
            changed.Add("local bootstrap port");
        }

        if (!string.Equals((previous.TunnelUser ?? string.Empty).Trim(), (current.TunnelUser ?? string.Empty).Trim(), StringComparison.Ordinal))
        {
            changed.Add("tunnel user");
        }

        if (!string.Equals(TunnelAuthMethods.Normalize(previous.TunnelAuthMethod), TunnelAuthMethods.Normalize(current.TunnelAuthMethod), StringComparison.Ordinal))
        {
            changed.Add("auth method");
        }

        if (!string.Equals((previous.TunnelPrivateKeyPath ?? string.Empty).Trim(), (current.TunnelPrivateKeyPath ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
        {
            changed.Add("private key path");
        }

        if (!string.Equals(previous.TunnelPrivateKeyPassphrase ?? string.Empty, current.TunnelPrivateKeyPassphrase ?? string.Empty, StringComparison.Ordinal))
        {
            changed.Add("private key passphrase");
        }

        if (!string.Equals(previous.TunnelPassword ?? string.Empty, current.TunnelPassword ?? string.Empty, StringComparison.Ordinal))
        {
            changed.Add("password");
        }

        var previousFrpProfiles = string.Join(
            "|",
            (previous.FrpServerProfiles ?? [])
                .Where(x => x is not null)
                .Select(x =>
                {
                    var host = (x.TunnelHost ?? string.Empty).Trim().ToLowerInvariant();
                    var port = x.FrpServerPort is > 0 and <= 65535 ? x.FrpServerPort : 7000;
                    var token = x.AuthToken ?? string.Empty;
                    return $"{host}:{port}:{token}";
                })
                .OrderBy(x => x, StringComparer.Ordinal));
        var currentFrpProfiles = string.Join(
            "|",
            (current.FrpServerProfiles ?? [])
                .Where(x => x is not null)
                .Select(x =>
                {
                    var host = (x.TunnelHost ?? string.Empty).Trim().ToLowerInvariant();
                    var port = x.FrpServerPort is > 0 and <= 65535 ? x.FrpServerPort : 7000;
                    var token = x.AuthToken ?? string.Empty;
                    return $"{host}:{port}:{token}";
                })
                .OrderBy(x => x, StringComparer.Ordinal));
        if (!string.Equals(previousFrpProfiles, currentFrpProfiles, StringComparison.Ordinal))
        {
            changed.Add("FRP server profiles");
        }

        summary = changed.Count == 0 ? string.Empty : string.Join(", ", changed);
        return changed.Count > 0;
    }

    private static ServiceConfig CloneConfig(ServiceConfig config)
    {
        return new ServiceConfig
        {
            SchemaVersion = config.SchemaVersion,
            GatewayType = GatewayTypes.Normalize(config.GatewayType),
            LocalProxyListenPort = config.LocalProxyListenPort,
            BootstrapSocksLocalPort = config.BootstrapSocksLocalPort,
            TunnelRemotePort = config.TunnelRemotePort,
            GatewayOnlineInstallEnabled = config.GatewayOnlineInstallEnabled,
            WhitelistAdapterIfIndex = config.WhitelistAdapterIfIndex,
            DefaultAdapterIfIndex = config.DefaultAdapterIfIndex,
            TunnelHost = config.TunnelHost,
            TunnelSshPort = config.TunnelSshPort,
            FrpServerPort = config.FrpServerPort,
            FrpRuntimeToken = config.FrpRuntimeToken,
            TunnelUser = config.TunnelUser,
            TunnelAuthMethod = config.TunnelAuthMethod,
            TunnelPrivateKeyPath = config.TunnelPrivateKeyPath,
            TunnelPrivateKeyPassphrase = config.TunnelPrivateKeyPassphrase,
            TunnelPassword = config.TunnelPassword,
            LicenseKey = config.LicenseKey,
            FrpServerProfiles = config.FrpServerProfiles?.Select(x => new FrpServerProfile
            {
                TunnelHost = x.TunnelHost ?? string.Empty,
                FrpServerPort = x.FrpServerPort,
                AuthToken = x.AuthToken ?? string.Empty
            }).ToList() ?? [],
            Relays = config.Relays?.Select(CloneRelayConfig).ToList() ?? [],
            LocalGateway = new LocalGatewayConfig
            {
                Protocol = LocalGatewayProtocols.Normalize(config.LocalGateway?.Protocol),
                Port = config.LocalGateway?.Port is > 0 and <= 65535 ? config.LocalGateway.Port : 443,
                BindAddress = string.IsNullOrWhiteSpace(config.LocalGateway?.BindAddress) ? "0.0.0.0" : config.LocalGateway.BindAddress.Trim(),
                RemoteAddress = string.IsNullOrWhiteSpace(config.LocalGateway?.RemoteAddress) ? string.Empty : config.LocalGateway.RemoteAddress.Trim(),
                Remark = string.IsNullOrWhiteSpace(config.LocalGateway?.Remark) ? "OmniRelay Local Gateway" : config.LocalGateway.Remark.Trim(),
                RuntimeEnabled = config.LocalGateway?.RuntimeEnabled ?? true
            }
        };
    }

    private static RelayConfig CloneRelayConfig(RelayConfig relay)
    {
        return new RelayConfig
        {
            Id = string.IsNullOrWhiteSpace(relay.Id) ? Guid.NewGuid().ToString("N") : relay.Id.Trim(),
            Name = string.IsNullOrWhiteSpace(relay.Name) ? "Relay" : relay.Name.Trim(),
            GatewayType = GatewayTypes.Normalize(relay.GatewayType),
            Enabled = relay.Enabled,
            IncomingAdapterId = relay.IncomingAdapterId ?? string.Empty,
            IncomingAdapterIfIndex = relay.IncomingAdapterIfIndex,
            OutgoingAdapterId = relay.OutgoingAdapterId ?? string.Empty,
            OutgoingAdapterIfIndex = relay.OutgoingAdapterIfIndex,
            DataPlaneLocalPort = relay.DataPlaneLocalPort,
            BootstrapSocksLocalPort = relay.BootstrapSocksLocalPort,
            FrpProfilePortOverride = relay.FrpProfilePortOverride is > 0 and <= 65535 ? relay.FrpProfilePortOverride : 7000,
            FrpProfileTokenOverride = relay.FrpProfileTokenOverride ?? string.Empty,
            OmniPanel = new RelayOmniPanelConfig
            {
                Port = relay.OmniPanel?.Port is > 0 and <= 65535
                    ? relay.OmniPanel.Port
                    : (relay.RemoteGateway?.PanelPort is > 0 and <= 65535 ? relay.RemoteGateway.PanelPort : 2054),
                Username = relay.OmniPanel?.Username ?? relay.RemoteGateway?.PanelUser ?? string.Empty,
                Password = relay.OmniPanel?.Password ?? relay.RemoteGateway?.PanelPassword ?? string.Empty,
                Domain = relay.OmniPanel?.Domain ?? relay.RemoteGateway?.PanelDomain ?? string.Empty,
                DomainOnly = relay.OmniPanel?.DomainOnly ?? relay.RemoteGateway?.PanelDomainOnly ?? false,
                UseSsl = relay.OmniPanel?.UseSsl ?? relay.RemoteGateway?.PanelUseSsl ?? false,
                SslMode = !string.IsNullOrWhiteSpace(relay.OmniPanel?.SslMode)
                    ? relay.OmniPanel.SslMode.Trim()
                    : (string.IsNullOrWhiteSpace(relay.RemoteGateway?.PanelSslMode) ? "letsencrypt" : relay.RemoteGateway.PanelSslMode.Trim()),
                UploadedCertPath = relay.OmniPanel?.UploadedCertPath ?? relay.RemoteGateway?.PanelUploadedCertPath ?? string.Empty,
                UploadedKeyPath = relay.OmniPanel?.UploadedKeyPath ?? relay.RemoteGateway?.PanelUploadedKeyPath ?? string.Empty,
                PublicUrl = relay.OmniPanel?.PublicUrl ?? string.Empty,
                LastError = relay.OmniPanel?.LastError ?? string.Empty
            },
            RemoteGateway = new RemoteGatewayConfig
            {
                TunnelHost = relay.RemoteGateway?.TunnelHost ?? string.Empty,
                TunnelSshPort = relay.RemoteGateway?.TunnelSshPort is > 0 and <= 65535 ? relay.RemoteGateway.TunnelSshPort : 22,
                TunnelRemotePort = relay.RemoteGateway?.TunnelRemotePort is > 0 and <= 65535 ? relay.RemoteGateway.TunnelRemotePort : 0,
                TunnelUser = string.IsNullOrWhiteSpace(relay.RemoteGateway?.TunnelUser) ? "OmniRelay" : relay.RemoteGateway.TunnelUser.Trim(),
                TunnelAuthMethod = TunnelAuthMethods.Normalize(relay.RemoteGateway?.TunnelAuthMethod),
                TunnelPrivateKeyPath = relay.RemoteGateway?.TunnelPrivateKeyPath ?? string.Empty,
                TunnelPrivateKeyPassphrase = relay.RemoteGateway?.TunnelPrivateKeyPassphrase ?? string.Empty,
                TunnelPassword = relay.RemoteGateway?.TunnelPassword ?? string.Empty,
                BootstrapMode = string.IsNullOrWhiteSpace(relay.RemoteGateway?.BootstrapMode) ? "tunnel" : relay.RemoteGateway.BootstrapMode.Trim(),
                Protocol = string.IsNullOrWhiteSpace(relay.RemoteGateway?.Protocol) ? "vless_tls_singbox" : relay.RemoteGateway.Protocol.Trim(),
                PublicPort = relay.RemoteGateway?.PublicPort is > 0 and <= 65535 ? relay.RemoteGateway.PublicPort : 443,
                PanelPort = relay.OmniPanel?.Port is > 0 and <= 65535
                    ? relay.OmniPanel.Port
                    : (relay.RemoteGateway?.PanelPort is > 0 and <= 65535 ? relay.RemoteGateway.PanelPort : 2054),
                PanelUser = relay.OmniPanel?.Username ?? relay.RemoteGateway?.PanelUser ?? string.Empty,
                PanelPassword = relay.OmniPanel?.Password ?? relay.RemoteGateway?.PanelPassword ?? string.Empty,
                PanelDomain = relay.OmniPanel?.Domain ?? relay.RemoteGateway?.PanelDomain ?? string.Empty,
                PanelDomainOnly = relay.OmniPanel?.DomainOnly ?? relay.RemoteGateway?.PanelDomainOnly ?? false,
                PanelUseSsl = relay.OmniPanel?.UseSsl ?? relay.RemoteGateway?.PanelUseSsl ?? false,
                PanelSslMode = !string.IsNullOrWhiteSpace(relay.OmniPanel?.SslMode)
                    ? relay.OmniPanel.SslMode.Trim()
                    : (string.IsNullOrWhiteSpace(relay.RemoteGateway?.PanelSslMode) ? "letsencrypt" : relay.RemoteGateway.PanelSslMode.Trim()),
                PanelUploadedCertPath = relay.OmniPanel?.UploadedCertPath ?? relay.RemoteGateway?.PanelUploadedCertPath ?? string.Empty,
                PanelUploadedKeyPath = relay.OmniPanel?.UploadedKeyPath ?? relay.RemoteGateway?.PanelUploadedKeyPath ?? string.Empty,
                ProtocolTlsEnabled = relay.RemoteGateway?.ProtocolTlsEnabled ?? false,
                ProtocolTlsServerName = relay.RemoteGateway?.ProtocolTlsServerName ?? string.Empty,
                ProtocolCertPath = relay.RemoteGateway?.ProtocolCertPath ?? string.Empty,
                ProtocolKeyPath = relay.RemoteGateway?.ProtocolKeyPath ?? string.Empty,
                ProtocolTlsMode = string.IsNullOrWhiteSpace(relay.RemoteGateway?.ProtocolTlsMode) ? "uploaded" : relay.RemoteGateway.ProtocolTlsMode.Trim(),
                ProtocolAlpnCsv = relay.RemoteGateway?.ProtocolAlpnCsv ?? string.Empty,
                ProxyUsername = string.IsNullOrWhiteSpace(relay.RemoteGateway?.ProxyUsername) ? "omni" : relay.RemoteGateway.ProxyUsername.Trim(),
                ProxyPassword = relay.RemoteGateway?.ProxyPassword ?? string.Empty,
                VlessTlsFlow = relay.RemoteGateway?.VlessTlsFlow ?? string.Empty,
                Hysteria2UpMbps = relay.RemoteGateway?.Hysteria2UpMbps is > 0 ? relay.RemoteGateway.Hysteria2UpMbps : 100,
                Hysteria2DownMbps = relay.RemoteGateway?.Hysteria2DownMbps is > 0 ? relay.RemoteGateway.Hysteria2DownMbps : 100,
                Hysteria2ObfsPassword = relay.RemoteGateway?.Hysteria2ObfsPassword ?? string.Empty,
                Hysteria2IgnoreClientBandwidth = relay.RemoteGateway?.Hysteria2IgnoreClientBandwidth ?? false,
                Hysteria2MasqueradeUrl = relay.RemoteGateway?.Hysteria2MasqueradeUrl ?? string.Empty,
                NaiveNetwork = relay.RemoteGateway?.NaiveNetwork ?? string.Empty,
                NaiveQuicCongestionControl = relay.RemoteGateway?.NaiveQuicCongestionControl ?? string.Empty,
                ShadowTlsCamouflageServer = relay.RemoteGateway?.ShadowTlsCamouflageServer ?? string.Empty,
                ShadowTlsStrictMode = relay.RemoteGateway?.ShadowTlsStrictMode ?? false,
                ShadowTlsWildcardSni = relay.RemoteGateway?.ShadowTlsWildcardSni ?? string.Empty,
                OpenVpnNetwork = string.IsNullOrWhiteSpace(relay.RemoteGateway?.OpenVpnNetwork) ? "10.29.0.0/24" : relay.RemoteGateway.OpenVpnNetwork.Trim(),
                OpenVpnSharedCaCertPath = relay.RemoteGateway?.OpenVpnSharedCaCertPath ?? string.Empty,
                OpenVpnSharedClientCertPath = relay.RemoteGateway?.OpenVpnSharedClientCertPath ?? string.Empty,
                OpenVpnSharedClientKeyPath = relay.RemoteGateway?.OpenVpnSharedClientKeyPath ?? string.Empty,
                OpenVpnSharedTlsCryptKeyPath = relay.RemoteGateway?.OpenVpnSharedTlsCryptKeyPath ?? string.Empty,
                DohEndpoints = relay.RemoteGateway?.DohEndpoints ?? string.Empty,
            },
            LocalGateway = new LocalGatewayConfig
            {
                Protocol = LocalGatewayProtocols.Normalize(relay.LocalGateway?.Protocol),
                Port = relay.LocalGateway?.Port is > 0 and <= 65535 ? relay.LocalGateway.Port : 443,
                BindAddress = string.IsNullOrWhiteSpace(relay.LocalGateway?.BindAddress) ? "0.0.0.0" : relay.LocalGateway.BindAddress.Trim(),
                RemoteAddress = string.IsNullOrWhiteSpace(relay.LocalGateway?.RemoteAddress) ? string.Empty : relay.LocalGateway.RemoteAddress.Trim(),
                Remark = string.IsNullOrWhiteSpace(relay.LocalGateway?.Remark) ? "OmniRelay Local Gateway" : relay.LocalGateway.Remark.Trim(),
                RuntimeEnabled = relay.LocalGateway?.RuntimeEnabled ?? true
            }
        };
    }

    private static void ValidateRelayPortIsolationOrThrow(IReadOnlyList<RelayConfig> relays)
    {
        var localPortOwners = new Dictionary<int, string>();
        var remotePortOwnersByHost = new Dictionary<string, Dictionary<int, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var relay in relays.Where(x => x.Enabled))
        {
            var relayToken = BuildRelayToken(relay);

            if (relay.DataPlaneLocalPort > 0)
            {
                EnsureUniquePort(localPortOwners, relay.DataPlaneLocalPort, relayToken, "local data-plane");
            }

            if (relay.BootstrapSocksLocalPort > 0)
            {
                EnsureUniquePort(localPortOwners, relay.BootstrapSocksLocalPort, relayToken, "local bootstrap");
            }

            if (string.Equals(GatewayTypes.Normalize(relay.GatewayType), GatewayTypes.Remote, StringComparison.OrdinalIgnoreCase))
            {
                var tunnelRemotePort = relay.RemoteGateway?.TunnelRemotePort ?? 0;
                if (tunnelRemotePort > 0)
                {
                    var host = (relay.RemoteGateway?.TunnelHost ?? string.Empty).Trim();
                    var hostKey = host.ToLowerInvariant();
                    if (!remotePortOwnersByHost.TryGetValue(hostKey, out var hostOwners))
                    {
                        hostOwners = new Dictionary<int, string>();
                        remotePortOwnersByHost[hostKey] = hostOwners;
                    }
                    EnsureUniquePort(hostOwners, tunnelRemotePort, relayToken, "remote data-plane");
                }

                // Legacy field: runtime now uses a single FRP data tunnel remote port.
                // Skip bootstrap isolation when it mirrors data port to avoid self-conflicts.
                // Removed: legacy remote bootstrap port validation.
            }
        }
    }

    private static void EnsureUniquePort(Dictionary<int, string> owners, int port, string relayToken, string plane)
    {
        if (!owners.TryAdd(port, relayToken))
        {
            throw new InvalidOperationException(
                $"Port isolation conflict on {plane} port {port}: relay '{relayToken}' conflicts with relay '{owners[port]}'.");
        }
    }

    private static void ValidateFrpProfilesForHostConsistencyOrThrow(ServiceConfig config)
    {
        var perHost = new Dictionary<string, (int Port, string Token)>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in (config.FrpServerProfiles ?? []).Where(x => x is not null))
        {
            var host = (profile.TunnelHost ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(host))
            {
                continue;
            }

            var key = host.ToLowerInvariant();
            var port = profile.FrpServerPort is > 0 and <= 65535 ? profile.FrpServerPort : 7000;
            var token = (profile.AuthToken ?? string.Empty).Trim();
            if (perHost.TryGetValue(key, out var current))
            {
                if (current.Port != port)
                {
                    throw new InvalidOperationException($"FRP profile conflict for host '{host}': multiple FRPS ports are configured.");
                }

                if (!string.IsNullOrWhiteSpace(current.Token) &&
                    !string.IsNullOrWhiteSpace(token) &&
                    !string.Equals(current.Token, token, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"FRP profile conflict for host '{host}': multiple FRP tokens are configured.");
                }
                continue;
            }

            perHost[key] = (port, token);
        }
    }

    private static string BuildRelayToken(RelayConfig relay)
    {
        var name = string.IsNullOrWhiteSpace(relay.Name) ? "relay" : relay.Name.Trim();
        var id = string.IsNullOrWhiteSpace(relay.Id) ? "no-id" : relay.Id.Trim();
        return $"{name} ({id})";
    }

    private static bool RequiresLocalGatewayRestart(ServiceConfig previous, ServiceConfig current)
    {
        var previousGatewayType = GatewayTypes.Normalize(previous.GatewayType);
        var currentGatewayType = GatewayTypes.Normalize(current.GatewayType);
        if (!string.Equals(previousGatewayType, currentGatewayType, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (previous.DefaultAdapterIfIndex != current.DefaultAdapterIfIndex)
        {
            return true;
        }

        var previousLocal = previous.LocalGateway ?? new LocalGatewayConfig();
        var currentLocal = current.LocalGateway ?? new LocalGatewayConfig();
        if (previousLocal.RuntimeEnabled != currentLocal.RuntimeEnabled)
        {
            return true;
        }

        if (!string.Equals(
                LocalGatewayProtocols.Normalize(previousLocal.Protocol),
                LocalGatewayProtocols.Normalize(currentLocal.Protocol),
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (previousLocal.Port != currentLocal.Port)
        {
            return true;
        }

        if (!string.Equals(
                (previousLocal.BindAddress ?? string.Empty).Trim(),
                (currentLocal.BindAddress ?? string.Empty).Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.Equals(
                (previousLocal.RemoteAddress ?? string.Empty).Trim(),
                (currentLocal.RemoteAddress ?? string.Empty).Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private List<LocalGatewayClient> LoadRelayLocalGatewayClientsFromDb(string relayId, string protocol)
    {
        var normalizedProtocol = LocalGatewayProtocols.Normalize(protocol);
        try
        {
            var dbPath = ServicePaths.GetRelayLocalGatewayAccountingDbPath(relayId);
            var dbDir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrWhiteSpace(dbDir))
            {
                Directory.CreateDirectory(dbDir);
            }

            using var connection = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
            connection.Open();
            EnsureRelayLocalGatewayClientSchema(connection);

            using var command = connection.CreateCommand();
            command.CommandText = """
SELECT c.client_id,
       COALESCE(c.email, ''),
       COALESCE(c.enabled, 1),
       COALESCE(c.remark, ''),
       COALESCE(c.auth_username, ''),
       COALESCE(c.auth_secret, ''),
       COALESCE(c.total_bytes_limit, 0),
       COALESCE(c.expiry_unix_ms, 0),
       COALESCE(x.speed_limit_kbps, 0),
       COALESCE(cc.last_seen_at, 0),
       COALESCE(cc.active_connections, 0),
       COALESCE(c.created_at, 0)
FROM clients c
LEFT JOIN relay_client_extensions x ON x.client_id = c.client_id
LEFT JOIN connection_counters cc ON cc.client_id = c.client_id
WHERE c.protocol_id = $protocol;
""";
            command.Parameters.AddWithValue("$protocol", normalizedProtocol);

            var clients = new List<LocalGatewayClient>();
            var usernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim();
                var email = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(email))
                {
                    continue;
                }

                var username = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                var secret = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
                var totalBytesLimit = reader.IsDBNull(6) ? 0L : reader.GetInt64(6);
                var createdAtUnix = reader.IsDBNull(11) ? 0L : reader.GetInt64(11);

                clients.Add(new LocalGatewayClient
                {
                    Id = id,
                    Email = email,
                    Enabled = !reader.IsDBNull(2) && reader.GetInt32(2) != 0,
                    Remark = string.IsNullOrWhiteSpace(reader.IsDBNull(3) ? null : reader.GetString(3)) ? email : reader.GetString(3).Trim(),
                    Protocol = normalizedProtocol,
                    Username = CreateLocalGatewayUsername(username, email, usernames),
                    Secret = string.IsNullOrWhiteSpace(secret) ? CreateLocalGatewaySecret(normalizedProtocol) : secret.Trim(),
                    TotalGB = totalBytesLimit > 0 ? totalBytesLimit / (1024d * 1024d * 1024d) : 0,
                    ExpiryTime = reader.IsDBNull(7) ? 0L : Math.Max(0L, reader.GetInt64(7)),
                    SpeedLimitKbps = reader.IsDBNull(8) ? 0 : Math.Max(0, reader.GetInt32(8)),
                    LastSeenAtUnixMs = reader.IsDBNull(9) ? 0L : Math.Max(0L, reader.GetInt64(9) * 1000L),
                    ActiveConnections = reader.IsDBNull(10) ? 0 : Math.Max(0, reader.GetInt32(10)),
                    CreatedAtUtc = createdAtUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(createdAtUnix) : DateTimeOffset.UtcNow
                });
            }

            return clients;
        }
        catch (Exception ex)
        {
            _log.Error("Failed loading local gateway clients.", ex);
            return [];
        }
    }

    private void PersistRelayLocalGatewayClientsToDb(string relayId, List<LocalGatewayClient> clients, string protocol)
    {
        try
        {
            var dbPath = ServicePaths.GetRelayLocalGatewayAccountingDbPath(relayId);
            var dbDir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrWhiteSpace(dbDir))
            {
                Directory.CreateDirectory(dbDir);
            }

            using var connection = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
            connection.Open();
            EnsureRelayLocalGatewayClientSchema(connection);

            using var tx = connection.BeginTransaction();
            var normalizedProtocol = LocalGatewayProtocols.Normalize(protocol);
            foreach (var client in clients)
            {
                client.Protocol = normalizedProtocol;

                using var upsertClient = connection.CreateCommand();
                upsertClient.Transaction = tx;
                upsertClient.CommandText = """
INSERT INTO clients(client_id, protocol_id, email, username, auth_username, auth_secret, enabled, remark, total_bytes_limit, expiry_unix_ms, created_at, updated_at)
VALUES($id, $protocol, $email, $username, $authUsername, $authSecret, $enabled, $remark, $totalBytesLimit, $expiry, $createdAt, $updatedAt)
ON CONFLICT(client_id) DO UPDATE SET
  protocol_id=excluded.protocol_id,
  email=excluded.email,
  username=excluded.username,
  auth_username=excluded.auth_username,
  auth_secret=excluded.auth_secret,
  enabled=excluded.enabled,
  remark=excluded.remark,
  total_bytes_limit=excluded.total_bytes_limit,
  expiry_unix_ms=excluded.expiry_unix_ms,
  updated_at=excluded.updated_at;
""";
                var totalBytesLimit = 0L;
                if (double.IsFinite(client.TotalGB) && client.TotalGB > 0)
                {
                    totalBytesLimit = checked((long)Math.Round(client.TotalGB * 1024d * 1024d * 1024d));
                }

                upsertClient.Parameters.AddWithValue("$id", client.Id ?? string.Empty);
                upsertClient.Parameters.AddWithValue("$protocol", normalizedProtocol);
                upsertClient.Parameters.AddWithValue("$email", (client.Email ?? string.Empty).Trim());
                upsertClient.Parameters.AddWithValue("$username", string.IsNullOrWhiteSpace(client.Username) ? (client.Id ?? string.Empty) : client.Username.Trim());
                upsertClient.Parameters.AddWithValue("$authUsername", string.IsNullOrWhiteSpace(client.Username) ? (client.Id ?? string.Empty) : client.Username.Trim());
                upsertClient.Parameters.AddWithValue("$authSecret", (client.Secret ?? string.Empty).Trim());
                upsertClient.Parameters.AddWithValue("$enabled", client.Enabled ? 1 : 0);
                upsertClient.Parameters.AddWithValue("$remark", string.IsNullOrWhiteSpace(client.Remark) ? (client.Email ?? string.Empty).Trim() : client.Remark.Trim());
                upsertClient.Parameters.AddWithValue("$totalBytesLimit", totalBytesLimit);
                upsertClient.Parameters.AddWithValue("$expiry", client.ExpiryTime < 0 ? 0L : client.ExpiryTime);
                upsertClient.Parameters.AddWithValue("$createdAt", client.CreatedAtUtc == default ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : client.CreatedAtUtc.ToUnixTimeSeconds());
                upsertClient.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                upsertClient.ExecuteNonQuery();

                using var upsertExt = connection.CreateCommand();
                upsertExt.Transaction = tx;
                upsertExt.CommandText = """
INSERT INTO relay_client_extensions(client_id, speed_limit_kbps, updated_at)
VALUES($id, $speedLimitKbps, $updatedAt)
ON CONFLICT(client_id) DO UPDATE SET
  speed_limit_kbps=excluded.speed_limit_kbps,
  updated_at=excluded.updated_at;
""";
                upsertExt.Parameters.AddWithValue("$id", client.Id ?? string.Empty);
                upsertExt.Parameters.AddWithValue("$speedLimitKbps", client.SpeedLimitKbps < 0 ? 0 : client.SpeedLimitKbps);
                upsertExt.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                upsertExt.ExecuteNonQuery();

                using var upsertUsage = connection.CreateCommand();
                upsertUsage.Transaction = tx;
                upsertUsage.CommandText = """
INSERT INTO usage_totals(client_id, used_bytes, updated_at)
VALUES($id, 0, 0)
ON CONFLICT(client_id) DO NOTHING;
""";
                upsertUsage.Parameters.AddWithValue("$id", client.Id ?? string.Empty);
                upsertUsage.ExecuteNonQuery();

                using var upsertConn = connection.CreateCommand();
                upsertConn.Transaction = tx;
                upsertConn.CommandText = """
INSERT INTO connection_counters(client_id, active_connections, last_seen_at)
VALUES($id, 0, 0)
ON CONFLICT(client_id) DO NOTHING;
""";
                upsertConn.Parameters.AddWithValue("$id", client.Id ?? string.Empty);
                upsertConn.ExecuteNonQuery();
            }

            var ids = clients
                .Select(x => (x.Id ?? string.Empty).Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var inList = ids.Length == 0
                ? "''"
                : string.Join(",", ids.Select((_, idx) => $"$id{idx}"));

            using var pruneClients = connection.CreateCommand();
            pruneClients.Transaction = tx;
            pruneClients.CommandText = $"DELETE FROM clients WHERE protocol_id = $protocol AND client_id NOT IN ({inList});";
            pruneClients.Parameters.AddWithValue("$protocol", normalizedProtocol);
            for (var i = 0; i < ids.Length; i++)
            {
                pruneClients.Parameters.AddWithValue($"$id{i}", ids[i]);
            }
            pruneClients.ExecuteNonQuery();

            using var pruneUsage = connection.CreateCommand();
            pruneUsage.Transaction = tx;
            pruneUsage.CommandText = "DELETE FROM usage_totals WHERE client_id NOT IN (SELECT client_id FROM clients);";
            pruneUsage.ExecuteNonQuery();

            using var pruneConnections = connection.CreateCommand();
            pruneConnections.Transaction = tx;
            pruneConnections.CommandText = "DELETE FROM connection_counters WHERE client_id NOT IN (SELECT client_id FROM clients);";
            pruneConnections.ExecuteNonQuery();

            using var pruneExtensions = connection.CreateCommand();
            pruneExtensions.Transaction = tx;
            pruneExtensions.CommandText = "DELETE FROM relay_client_extensions WHERE client_id NOT IN (SELECT client_id FROM clients);";
            pruneExtensions.ExecuteNonQuery();

            tx.Commit();
        }
        catch (Exception ex)
        {
            _log.Error("Failed persisting relay local gateway clients.", ex);
        }
    }

    private static void EnsureRelayLocalGatewayClientSchema(SqliteConnection connection)
    {
        using var schema = connection.CreateCommand();
        schema.CommandText = """
CREATE TABLE IF NOT EXISTS clients (
  client_id TEXT PRIMARY KEY,
  protocol_id TEXT NOT NULL,
  email TEXT NOT NULL DEFAULT '',
  username TEXT NOT NULL DEFAULT '',
  auth_username TEXT NOT NULL DEFAULT '',
  auth_secret TEXT NOT NULL DEFAULT '',
  enabled INTEGER NOT NULL DEFAULT 1,
  remark TEXT NOT NULL DEFAULT '',
  total_bytes_limit INTEGER NOT NULL DEFAULT 0,
  expiry_unix_ms INTEGER NOT NULL DEFAULT 0,
  created_at INTEGER NOT NULL DEFAULT 0,
  updated_at INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS usage_totals (
  client_id TEXT PRIMARY KEY,
  used_bytes INTEGER NOT NULL DEFAULT 0,
  updated_at INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS connection_counters (
  client_id TEXT PRIMARY KEY,
  active_connections INTEGER NOT NULL DEFAULT 0,
  last_seen_at INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS relay_client_extensions (
  client_id TEXT PRIMARY KEY,
  speed_limit_kbps INTEGER NOT NULL DEFAULT 0,
  updated_at INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_clients_protocol_id ON clients(protocol_id);
CREATE INDEX IF NOT EXISTS idx_clients_protocol_auth_username ON clients(protocol_id, auth_username);
""";
        schema.ExecuteNonQuery();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(clients);";
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = pragma.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!reader.IsDBNull(1))
                {
                    cols.Add(reader.GetString(1));
                }
            }
        }

        static void EnsureColumn(SqliteConnection conn, HashSet<string> existing, string name, string ddl)
        {
            if (existing.Contains(name))
            {
                return;
            }

            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE clients ADD COLUMN {ddl};";
            alter.ExecuteNonQuery();
            existing.Add(name);
        }

        EnsureColumn(connection, cols, "email", "email TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, cols, "username", "username TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, cols, "auth_username", "auth_username TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, cols, "auth_secret", "auth_secret TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, cols, "remark", "remark TEXT NOT NULL DEFAULT ''");
    }

    private void SyncRelayLocalGatewayAccountingDb(string relayId, string protocol, IReadOnlyList<LocalGatewayClient> clients)
    {
        try
        {
            var signature = BuildRelayLocalClientsSyncSignature(protocol, clients);
            if (_relayLocalAccountingSyncSignatures.TryGetValue(relayId, out var existingSignature) &&
                string.Equals(existingSignature, signature, StringComparison.Ordinal))
            {
                return;
            }

            var dbPath = ServicePaths.GetRelayLocalGatewayAccountingDbPath(relayId);
            var dbDir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrWhiteSpace(dbDir))
            {
                Directory.CreateDirectory(dbDir);
            }

            using var connection = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
            connection.Open();

            using (var schema = connection.CreateCommand())
            {
                schema.CommandText = """
CREATE TABLE IF NOT EXISTS clients (
  client_id TEXT PRIMARY KEY,
  protocol_id TEXT NOT NULL,
  username TEXT NOT NULL,
  enabled INTEGER NOT NULL DEFAULT 1,
  total_bytes_limit INTEGER NOT NULL DEFAULT 0,
  expiry_unix_ms INTEGER NOT NULL DEFAULT 0,
  created_at INTEGER NOT NULL DEFAULT 0,
  updated_at INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS usage_totals (
  client_id TEXT PRIMARY KEY,
  used_bytes INTEGER NOT NULL DEFAULT 0,
  updated_at INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS connection_counters (
  client_id TEXT PRIMARY KEY,
  active_connections INTEGER NOT NULL DEFAULT 0,
  last_seen_at INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS relay_client_extensions (
  client_id TEXT PRIMARY KEY,
  speed_limit_kbps INTEGER NOT NULL DEFAULT 0,
  updated_at INTEGER NOT NULL DEFAULT 0
);
""";
                schema.ExecuteNonQuery();
            }

            var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var tx = connection.BeginTransaction();
            foreach (var client in clients)
            {
                using var upsertClient = connection.CreateCommand();
                upsertClient.Transaction = tx;
                upsertClient.CommandText = """
INSERT INTO clients(client_id, protocol_id, username, enabled, total_bytes_limit, expiry_unix_ms, created_at, updated_at)
VALUES($id, $protocol, $username, $enabled, $totalBytesLimit, $expiry, $createdAt, $updatedAt)
ON CONFLICT(client_id) DO UPDATE SET
  protocol_id=excluded.protocol_id,
  username=excluded.username,
  enabled=excluded.enabled,
  total_bytes_limit=excluded.total_bytes_limit,
  expiry_unix_ms=excluded.expiry_unix_ms,
  updated_at=excluded.updated_at;
""";
                upsertClient.Parameters.AddWithValue("$id", client.Id ?? string.Empty);
                upsertClient.Parameters.AddWithValue("$protocol", LocalGatewayProtocols.Normalize(protocol));
                upsertClient.Parameters.AddWithValue("$username", string.IsNullOrWhiteSpace(client.Username) ? client.Id ?? string.Empty : client.Username);
                upsertClient.Parameters.AddWithValue("$enabled", client.Enabled ? 1 : 0);
                var totalBytesLimit = 0L;
                if (double.IsFinite(client.TotalGB) && client.TotalGB > 0)
                {
                    totalBytesLimit = checked((long)Math.Round(client.TotalGB * 1024d * 1024d * 1024d));
                }
                upsertClient.Parameters.AddWithValue("$totalBytesLimit", totalBytesLimit);
                upsertClient.Parameters.AddWithValue("$expiry", client.ExpiryTime < 0 ? 0L : client.ExpiryTime);
                upsertClient.Parameters.AddWithValue("$createdAt", client.CreatedAtUtc.ToUnixTimeSeconds());
                upsertClient.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                upsertClient.ExecuteNonQuery();

                using var upsertExt = connection.CreateCommand();
                upsertExt.Transaction = tx;
                upsertExt.CommandText = """
INSERT INTO relay_client_extensions(client_id, speed_limit_kbps, updated_at)
VALUES($id, $speedLimitKbps, $updatedAt)
ON CONFLICT(client_id) DO UPDATE SET
  speed_limit_kbps=excluded.speed_limit_kbps,
  updated_at=excluded.updated_at;
""";
                upsertExt.Parameters.AddWithValue("$id", client.Id ?? string.Empty);
                upsertExt.Parameters.AddWithValue("$speedLimitKbps", client.SpeedLimitKbps < 0 ? 0 : client.SpeedLimitKbps);
                upsertExt.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                upsertExt.ExecuteNonQuery();

                using var upsertUsage = connection.CreateCommand();
                upsertUsage.Transaction = tx;
                upsertUsage.CommandText = """
INSERT INTO usage_totals(client_id, used_bytes)
VALUES($id, 0)
ON CONFLICT(client_id) DO NOTHING;
""";
                upsertUsage.Parameters.AddWithValue("$id", client.Id ?? string.Empty);
                upsertUsage.ExecuteNonQuery();

                using var upsertConn = connection.CreateCommand();
                upsertConn.Transaction = tx;
                upsertConn.CommandText = """
INSERT INTO connection_counters(client_id, active_connections, last_seen_at)
VALUES($id, 0, 0)
ON CONFLICT(client_id) DO NOTHING;
""";
                upsertConn.Parameters.AddWithValue("$id", client.Id ?? string.Empty);
                upsertConn.ExecuteNonQuery();
            }

            var ids = clients
                .Select(x => (x.Id ?? string.Empty).Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var inList = ids.Length == 0
                ? "''"
                : string.Join(",", ids.Select((_, idx) => $"$id{idx}"));

            using var pruneClients = connection.CreateCommand();
            pruneClients.Transaction = tx;
            pruneClients.CommandText = $"DELETE FROM clients WHERE client_id NOT IN ({inList});";
            for (var i = 0; i < ids.Length; i++)
            {
                pruneClients.Parameters.AddWithValue($"$id{i}", ids[i]);
            }
            pruneClients.ExecuteNonQuery();

            using var pruneUsage = connection.CreateCommand();
            pruneUsage.Transaction = tx;
            pruneUsage.CommandText = "DELETE FROM usage_totals WHERE client_id NOT IN (SELECT client_id FROM clients);";
            pruneUsage.ExecuteNonQuery();

            using var pruneConnections = connection.CreateCommand();
            pruneConnections.Transaction = tx;
            pruneConnections.CommandText = "DELETE FROM connection_counters WHERE client_id NOT IN (SELECT client_id FROM clients);";
            pruneConnections.ExecuteNonQuery();

            using var pruneExtensions = connection.CreateCommand();
            pruneExtensions.Transaction = tx;
            pruneExtensions.CommandText = "DELETE FROM relay_client_extensions WHERE client_id NOT IN (SELECT client_id FROM clients);";
            pruneExtensions.ExecuteNonQuery();

            tx.Commit();
            _relayLocalAccountingSyncSignatures[relayId] = signature;
        }
        catch (Exception ex)
        {
            _log.Error($"Failed syncing local accounting DB for relay '{relayId}'.", ex);
        }
    }

    private static string BuildRelayLocalClientsSyncSignature(string protocol, IReadOnlyList<LocalGatewayClient> clients)
    {
        var normalizedProtocol = LocalGatewayProtocols.Normalize(protocol);
        var ordered = clients
            .OrderBy(x => x.Id, StringComparer.Ordinal)
            .Select(x =>
                $"{x.Id}|{x.Email}|{(x.Enabled ? 1 : 0)}|{(double.IsFinite(x.TotalGB) && x.TotalGB >= 0 ? x.TotalGB.ToString("R", CultureInfo.InvariantCulture) : "0")}|{(x.ExpiryTime < 0 ? 0 : x.ExpiryTime)}|{(x.SpeedLimitKbps < 0 ? 0 : x.SpeedLimitKbps)}");

        return normalizedProtocol + "||" + string.Join("||", ordered);
    }

    private void ApplyRelayLocalGatewayAccountingSnapshot(string relayId, List<LocalGatewayClient> clients)
    {
        if (clients.Count == 0)
        {
            return;
        }

        try
        {
            var dbPath = ServicePaths.GetRelayLocalGatewayAccountingDbPath(relayId);
            if (!File.Exists(dbPath))
            {
                return;
            }

            using var connection = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
SELECT c.client_id,
       COALESCE(u.used_bytes, 0),
       COALESCE(cc.last_seen_at, 0),
       COALESCE(cc.active_connections, 0),
       COALESCE(c.total_bytes_limit, 0),
       COALESCE(c.expiry_unix_ms, 0),
       COALESCE(x.speed_limit_kbps, 0),
       COALESCE(c.enabled, 1)
FROM clients c
LEFT JOIN usage_totals u ON u.client_id = c.client_id
LEFT JOIN connection_counters cc ON cc.client_id = c.client_id
LEFT JOIN relay_client_extensions x ON x.client_id = c.client_id;
""";

            var byId = clients.ToDictionary(x => x.Id, StringComparer.Ordinal);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var clientId = reader.GetString(0);
                if (!byId.TryGetValue(clientId, out var client))
                {
                    continue;
                }

                _ = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
                var lastSeenAtUnixSeconds = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);
                var activeConnections = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
                var totalBytesLimit = reader.IsDBNull(4) ? 0L : reader.GetInt64(4);
                var expiryUnixMs = reader.IsDBNull(5) ? 0L : reader.GetInt64(5);
                var speedLimitKbps = reader.IsDBNull(6) ? 0 : reader.GetInt32(6);
                var enabledRaw = reader.IsDBNull(7) ? 1 : reader.GetInt32(7);

                client.LastSeenAtUnixMs = lastSeenAtUnixSeconds > 0 ? lastSeenAtUnixSeconds * 1000 : 0;
                client.ActiveConnections = activeConnections < 0 ? 0 : activeConnections;
                client.TotalGB = totalBytesLimit > 0 ? totalBytesLimit / (1024d * 1024d * 1024d) : 0;
                client.ExpiryTime = expiryUnixMs < 0 ? 0 : expiryUnixMs;
                client.SpeedLimitKbps = speedLimitKbps < 0 ? 0 : speedLimitKbps;
                client.Enabled = enabledRaw != 0;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Failed reading local accounting DB for relay '{relayId}'.", ex);
        }
    }

    private static bool TryCreateLocalGatewayClient(
        List<LocalGatewayClient> clients,
        string protocol,
        string email,
        string? remark,
        out LocalGatewayClient? client,
        out string? error)
    {
        var normalizedEmail = (email ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            client = null;
            error = "Client email is required.";
            return false;
        }

        if (clients.Any(x => string.Equals(x.Email, normalizedEmail, StringComparison.OrdinalIgnoreCase)))
        {
            client = null;
            error = "Client email already exists.";
            return false;
        }

        var normalizedProtocol = LocalGatewayProtocols.Normalize(protocol);
        var existingUsernames = clients
            .Select(x => x.Username)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        client = new LocalGatewayClient
        {
            Id = Guid.NewGuid().ToString(),
            Email = normalizedEmail,
            Enabled = true,
            Remark = string.IsNullOrWhiteSpace(remark) ? normalizedEmail : remark.Trim(),
            Protocol = normalizedProtocol,
            Username = CreateLocalGatewayUsername(normalizedEmail, existingUsernames),
            Secret = CreateLocalGatewaySecret(normalizedProtocol),
            TotalGB = 0,
            ExpiryTime = 0,
            SpeedLimitKbps = 0,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        clients.Add(client);
        error = null;
        return true;
    }

    private void SetRelayLocalClientCountLocked(string relayId, int count)
    {
        if (_relayStatuses.TryGetValue(relayId, out var status))
        {
            status.LocalGatewayClientsCount = count;
            status.LastStatusUpdateUtc = DateTimeOffset.UtcNow;
        }
    }

    private bool TryGetRelayLocalContextLocked(string? relayId, out RelayConfig relay, out string protocol, out string? error)
    {
        relay = new RelayConfig();
        protocol = LocalGatewayProtocols.VlessTcpPlain;
        error = null;
        var id = (relayId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            error = "Relay id is required.";
            return false;
        }

        var found = _config.Relays.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
        if (found is null)
        {
            error = "Relay not found.";
            return false;
        }

        relay = CloneRelayConfig(found);
        protocol = LocalGatewayProtocols.Normalize(relay.LocalGateway.Protocol);
        return true;
    }

    private static LocalGatewayClient CloneLocalGatewayClient(LocalGatewayClient client)
    {
        return new LocalGatewayClient
        {
            Id = client.Id,
            Email = client.Email,
            Enabled = client.Enabled,
            Remark = client.Remark,
            Protocol = client.Protocol,
            Username = client.Username,
            Secret = client.Secret,
            TotalGB = client.TotalGB,
            ExpiryTime = client.ExpiryTime,
            SpeedLimitKbps = client.SpeedLimitKbps,
            LastSeenAtUnixMs = client.LastSeenAtUnixMs,
            ActiveConnections = client.ActiveConnections,
            CreatedAtUtc = client.CreatedAtUtc
        };
    }

    private static string CreateLocalGatewaySecret(string protocol)
    {
        var normalized = LocalGatewayProtocols.Normalize(protocol);
        if (string.Equals(normalized, LocalGatewayProtocols.Shadowsocks, StringComparison.OrdinalIgnoreCase))
        {
            return Convert.ToHexString(Guid.NewGuid().ToByteArray());
        }
        if (string.Equals(normalized, LocalGatewayProtocols.OpenVpnTcp, StringComparison.OrdinalIgnoreCase))
        {
            return CreateRandomAlphaNum(24);
        }

        return Guid.NewGuid().ToString();
    }

    private string ResolveLocalGatewayHostLocked()
    {
        var host = (_config.LocalGateway.RemoteAddress ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(host))
        {
            return host;
        }

        host = (_status.WhitelistAdapterIp ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            host = (_status.DefaultAdapterIp ?? string.Empty).Trim();
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            host = "127.0.0.1";
        }

        return host;
    }

    private static string ResolveLocalGatewayHostLocked(RelayConfig relay)
    {
        var host = (relay.LocalGateway.RemoteAddress ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(host))
        {
            return host;
        }

        if (NetworkAdapterCatalog.TryGetPrimaryIpv4(relay.IncomingAdapterId, relay.IncomingAdapterIfIndex, out var incomingIp, out _) && incomingIp is not null)
        {
            return incomingIp.ToString();
        }

        if (NetworkAdapterCatalog.TryGetPrimaryIpv4(relay.OutgoingAdapterId, relay.OutgoingAdapterIfIndex, out var outgoingIp, out _) && outgoingIp is not null)
        {
            return outgoingIp.ToString();
        }

        return "127.0.0.1";
    }

    private static LocalGatewayClientConfigPayload BuildLocalGatewayClientConfigPayload(
        LocalGatewayConfig config,
        LocalGatewayClient client,
        string host,
        string protocol,
        string relayId,
        out string? error)
    {
        var normalizedProtocol = LocalGatewayProtocols.Normalize(protocol);
        var port = config.Port;
        var display = string.IsNullOrWhiteSpace(client.Remark) ? client.Email : client.Remark;
        if (string.Equals(normalizedProtocol, LocalGatewayProtocols.Shadowsocks, StringComparison.OrdinalIgnoreCase))
        {
            const string method = "aes-128-gcm";
            var userInfo = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{method}:{client.Secret}"))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            error = null;
            return new LocalGatewayClientConfigPayload
            {
                Mode = "uri",
                Uri = $"ss://{userInfo}@{host}:{port}#{Uri.EscapeDataString(display)}",
                Title = "Shadowsocks Config"
            };
        }

        if (string.Equals(normalizedProtocol, LocalGatewayProtocols.OpenVpnTcp, StringComparison.OrdinalIgnoreCase))
        {
            var username = string.IsNullOrWhiteSpace(client.Username)
                ? CreateLocalGatewayUsername(client.Email, new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                : client.Username.Trim();
            if (string.IsNullOrWhiteSpace(username))
            {
                error = "OpenVPN username is missing.";
                return LocalGatewayClientConfigPayload.Empty;
            }

            if (!File.Exists(ServicePaths.GetRelayLocalGatewayOpenVpnCaPath(relayId)))
            {
                error = "OpenVPN CA material was not generated yet. Start local OpenVPN runtime first.";
                return LocalGatewayClientConfigPayload.Empty;
            }

            if (!File.Exists(ServicePaths.GetRelayLocalGatewayOpenVpnTlsCryptKeyPath(relayId)))
            {
                error = "OpenVPN TLS key material was not generated yet. Start local OpenVPN runtime first.";
                return LocalGatewayClientConfigPayload.Empty;
            }
            if (!File.Exists(ServicePaths.GetRelayLocalGatewayOpenVpnServerCertPath(relayId)))
            {
                error = "OpenVPN shared cert material is missing. Configure relay OpenVPN bundle and start runtime first.";
                return LocalGatewayClientConfigPayload.Empty;
            }
            if (!File.Exists(ServicePaths.GetRelayLocalGatewayOpenVpnServerKeyPath(relayId)))
            {
                error = "OpenVPN shared key material is missing. Configure relay OpenVPN bundle and start runtime first.";
                return LocalGatewayClientConfigPayload.Empty;
            }

            var ovpn = BuildOpenVpnClientProfile(host, port, relayId);
            var safeStem = ToSafeFileStem(display);
            error = null;
            return new LocalGatewayClientConfigPayload
            {
                Mode = "openvpn_bundle",
                Title = "OpenVPN Client Bundle",
                Uri = ovpn.Trim(),
                Username = username,
                Password = client.Secret,
                OvpnFileName = $"{safeStem}-{client.Id[..Math.Min(8, client.Id.Length)]}.ovpn",
                OvpnContent = ovpn
            };
        }

        var query = "type=tcp&security=none&encryption=none";
        error = null;
        return new LocalGatewayClientConfigPayload
        {
            Mode = "uri",
            Uri = $"vless://{client.Id}@{host}:{port}?{query}#{Uri.EscapeDataString(display)}",
            Title = "VLESS Config"
        };
    }

    private static string BuildOpenVpnClientProfile(string host, int port, string relayId)
    {
        var caPem = File.ReadAllText(ServicePaths.GetRelayLocalGatewayOpenVpnCaPath(relayId)).Trim();
        var certPem = File.ReadAllText(ServicePaths.GetRelayLocalGatewayOpenVpnServerCertPath(relayId)).Trim();
        var keyPem = File.ReadAllText(ServicePaths.GetRelayLocalGatewayOpenVpnServerKeyPath(relayId)).Trim();
        var tlsCrypt = File.ReadAllText(ServicePaths.GetRelayLocalGatewayOpenVpnTlsCryptKeyPath(relayId)).Trim();
        var sb = new StringBuilder(2048);
        sb.AppendLine("client");
        sb.AppendLine("dev tun");
        sb.AppendLine("proto tcp-client");
        sb.AppendLine($"remote {host} {port}");
        sb.AppendLine("nobind");
        sb.AppendLine("persist-key");
        sb.AppendLine("persist-tun");
        sb.AppendLine("remote-cert-tls server");
        sb.AppendLine("setenv CLIENT_CERT 0");
        sb.AppendLine("auth-user-pass");
        sb.AppendLine("auth-nocache");
        sb.AppendLine("auth SHA256");
        sb.AppendLine("cipher AES-256-GCM");
        sb.AppendLine("data-ciphers AES-256-GCM:AES-128-GCM");
        sb.AppendLine("data-ciphers-fallback AES-256-GCM");
        sb.AppendLine("verb 3");
        sb.AppendLine("<ca>");
        sb.AppendLine(caPem);
        sb.AppendLine("</ca>");
        sb.AppendLine("<cert>");
        sb.AppendLine(certPem);
        sb.AppendLine("</cert>");
        sb.AppendLine("<key>");
        sb.AppendLine(keyPem);
        sb.AppendLine("</key>");
        sb.AppendLine("<tls-crypt>");
        sb.AppendLine(tlsCrypt);
        sb.AppendLine("</tls-crypt>");
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string CreateLocalGatewayUsername(string preferred, string email, ISet<string> existingUsernames)
    {
        var normalizedPreferred = NormalizeUsernameSeed(preferred);
        if (!string.IsNullOrWhiteSpace(normalizedPreferred) && existingUsernames.Add(normalizedPreferred))
        {
            return normalizedPreferred;
        }

        var baseName = "ovpn_" + NormalizeUsernameSeed(email);
        if (string.IsNullOrWhiteSpace(baseName) || string.Equals(baseName, "ovpn_", StringComparison.Ordinal))
        {
            baseName = "ovpn_client";
        }

        if (existingUsernames.Add(baseName))
        {
            return baseName;
        }

        for (var i = 0; i < 100; i++)
        {
            var candidate = $"{baseName}{CreateRandomAlphaNum(4).ToLowerInvariant()}";
            if (existingUsernames.Add(candidate))
            {
                return candidate;
            }
        }

        var fallback = $"ovpn_{CreateRandomAlphaNum(10).ToLowerInvariant()}";
        existingUsernames.Add(fallback);
        return fallback;
    }

    private static string CreateLocalGatewayUsername(string email, ISet<string> existingUsernames)
    {
        return CreateLocalGatewayUsername(string.Empty, email, existingUsernames);
    }

    private static string NormalizeUsernameSeed(string? seed)
    {
        var raw = (seed ?? string.Empty).Trim().ToLowerInvariant();
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        var chars = raw.Where(char.IsAsciiLetterOrDigit).Take(18).ToArray();
        return new string(chars);
    }

    private static string CreateRandomAlphaNum(int length)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var bytes = RandomNumberGenerator.GetBytes(Math.Max(1, length));
        var chars = new char[Math.Max(1, length)];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        }

        return new string(chars);
    }

    private static string ToSafeFileStem(string value)
    {
        var safe = new string((value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-')
            .Where(ch => ch != '\0')
            .ToArray())
            .Trim('-');
        if (string.IsNullOrWhiteSpace(safe))
        {
            return "openvpn-client";
        }

        return safe.Length <= 40 ? safe : safe[..40];
    }

    private void ApplyPolicySnapshotLocked(PolicyStoreSnapshot snapshot)
    {
        _policyRevision = snapshot.Revision;
        _policyUpdatedAtUtc = snapshot.UpdatedAtUtc;
        _whitelistEntries = snapshot.WhitelistEntries
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _blacklistEntries = snapshot.BlacklistEntries
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _whitelistIndex = BuildIndex(_whitelistEntries, PolicyListTypes.Whitelist);
        _blacklistIndex = BuildIndex(_blacklistEntries, PolicyListTypes.Blacklist);
        _status.WhitelistCount = _whitelistEntries.Count;
        _status.BlacklistCount = _blacklistEntries.Count;
    }

    private void UpdateRelayPolicyStatusLocked(string relayId)
    {
        var snapshot = _policyStore.LoadPolicySet(relayId);
        if (string.IsNullOrWhiteSpace(relayId))
        {
            _status.WhitelistCount = snapshot.WhitelistListCount;
            _status.BlacklistCount = snapshot.BlacklistListCount;
            return;
        }

        if (_relayStatuses.TryGetValue(relayId, out var status))
        {
            status.WhitelistCount = snapshot.WhitelistListCount;
            status.BlacklistCount = snapshot.BlacklistListCount;
            status.LastStatusUpdateUtc = DateTimeOffset.UtcNow;
        }
    }

    private IReadOnlyList<RelayPolicyCompiledList> BuildCompiledPolicyLists(RelayPolicySetSnapshot snapshot)
    {
        var compiled = new List<RelayPolicyCompiledList>(snapshot.Lists.Count);
        foreach (var list in snapshot.Lists.OrderBy(x => x.Priority))
        {
            var index = BuildIndex(list.Entries, list.ListType);
            compiled.Add(new RelayPolicyCompiledList(
                list.ListId,
                list.Label,
                list.ListType,
                list.Priority,
                index));
        }

        return compiled;
    }

    private PolicyAddressIndex BuildIndex(IReadOnlyList<string> entries, string listType)
    {
        var rules = new List<NetworkRule>(entries.Count);
        var errors = new List<string>();
        foreach (var entry in entries)
        {
            if (NetworkRule.TryParse(entry, out var rule, out var error) && rule is not null)
            {
                rules.Add(rule);
            }
            else if (!string.IsNullOrWhiteSpace(error))
            {
                errors.Add(error);
            }
        }

        if (errors.Count > 0)
        {
            _log.Warn($"Invalid persisted {listType} entries ignored: {string.Join("; ", errors.Take(12))}");
        }

        return PolicyAddressIndex.Build(rules);
    }
}



