namespace OmniRelay.Service.Runtime;

public static class ServicePaths
{
    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "OmniRelay");

    public static string ConfigPath { get; } = Path.Combine(RootDirectory, "config.json");
    public static string PolicyDbPath { get; } = Path.Combine(RootDirectory, "policy.db");
    public static string LicenseCachePath { get; } = Path.Combine(RootDirectory, "license.cache");
    public static string LicensePublicKeysCachePath { get; } = Path.Combine(RootDirectory, "license_public_keys.cache");
    public static string LicenseCertificatePath { get; } = Path.Combine(RootDirectory, "license_certificate.cache");
    public static string LogsDirectory { get; } = Path.Combine(RootDirectory, "logs");
    public static string ServiceLogPath { get; } = Path.Combine(LogsDirectory, "service.log");
    public static string RelayGatewayRuntimeRootDirectory { get; } = Path.Combine(RootDirectory, "local-gateway", "relays");
    public static string OmniPanelDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "omnipanel");

    public static string ResolveLocalSingBoxExecutablePath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "sing-box", "sing-box.exe"),
            Path.Combine(AppContext.BaseDirectory, "sing-box.exe"),
            Path.Combine(AppContext.BaseDirectory, "singbox", "singbox.exe"),
            Path.Combine(AppContext.BaseDirectory, "singbox.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return candidates[0];
    }

    public static string ResolveConnectorCoreExecutablePath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "connector-core", "connector-core.exe"),
            Path.Combine(AppContext.BaseDirectory, "connector-core.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return candidates[0];
    }

    public static string ResolveOpenVpnExecutablePath()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenVPN", "bin", "openvpn.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "OpenVPN", "bin", "openvpn.exe"),
            Path.Combine(AppContext.BaseDirectory, "openvpn", "openvpn.exe"),
            Path.Combine(AppContext.BaseDirectory, "openvpn.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return candidates[0];
    }

    public static string ResolveOmniPanelServerJsPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "omnipanel", "server.js"),
            Path.Combine(AppContext.BaseDirectory, "gateway-panel", "server.js"),
            Path.Combine(AppContext.BaseDirectory, "GatewayPanel", "server.js")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return candidates[0];
    }

    public static string ResolveNodeExecutablePath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "nodejs", "node.exe"),
            Path.Combine(AppContext.BaseDirectory, "node.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "node";
    }

    public static string ResolveOpenVpnDriverInstallerPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "openvpn", "OpenVPNInstaller.msi"),
            Path.Combine(AppContext.BaseDirectory, "openvpn", "OpenVPNDriverInstaller.msi"),
            Path.Combine(AppContext.BaseDirectory, "openvpn", "OpenVPNDriverInstaller.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return candidates[0];
    }

    public static string ResolveSqlite3ExecutablePath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "sqlite3", "sqlite3.exe"),
            Path.Combine(AppContext.BaseDirectory, "sqlite3.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "sqlite3";
    }

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(RelayGatewayRuntimeRootDirectory);
    }

    public static string GetRelayLocalGatewayClientsPath(string relayId, string? protocol)
    {
        var directory = GetRelayLocalGatewayDirectory(relayId);
        Directory.CreateDirectory(directory);
        var normalized = OmniRelay.Core.Configuration.LocalGatewayProtocols.Normalize(protocol);
        return Path.Combine(directory, $"clients.{normalized}.json");
    }

    public static string GetRelayLocalGatewayDirectory(string relayId)
    {
        var safeRelayId = string.IsNullOrWhiteSpace(relayId)
            ? "default"
            : new string(relayId.Trim().Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        if (string.IsNullOrWhiteSpace(safeRelayId))
        {
            safeRelayId = "default";
        }

        return Path.Combine(RelayGatewayRuntimeRootDirectory, safeRelayId);
    }

    public static string GetRelayLocalGatewaySingBoxConfigPath(string relayId)
    {
        return Path.Combine(GetRelayLocalGatewayDirectory(relayId), "singbox.config.json");
    }

    public static string GetRelayLocalGatewayConnectorMetadataPath(string relayId)
    {
        return Path.Combine(GetRelayLocalGatewayDirectory(relayId), "connector.metadata.json");
    }

    public static string GetRelayLocalGatewayConnectorStatePath(string relayId)
    {
        return Path.Combine(GetRelayLocalGatewayDirectory(relayId), "connector.state.json");
    }

    public static string GetRelayLocalGatewayConnectorLockPath(string relayId)
    {
        return Path.Combine(GetRelayLocalGatewayDirectory(relayId), "connector.lock");
    }

    public static string GetRelayLocalGatewaySingBoxStdoutLogPath(string relayId)
    {
        return Path.Combine(GetRelayLocalGatewayDirectory(relayId), "singbox.stdout.log");
    }

    public static string GetRelayLocalGatewaySingBoxStderrLogPath(string relayId)
    {
        return Path.Combine(GetRelayLocalGatewayDirectory(relayId), "singbox.stderr.log");
    }

    public static string GetRelayLocalGatewayOpenVpnDirectory(string relayId)
    {
        return Path.Combine(GetRelayLocalGatewayDirectory(relayId), "openvpn");
    }

    public static string GetRelayLocalGatewayOpenVpnServerConfigPath(string relayId)
    {
        return Path.Combine(GetRelayLocalGatewayOpenVpnDirectory(relayId), "server.conf");
    }

    public static string GetRelayLocalGatewayOpenVpnAuthFilePath(string relayId)
    {
        return Path.Combine(GetRelayLocalGatewayOpenVpnDirectory(relayId), "openvpn.auth.txt");
    }

    public static string GetRelayLocalGatewayOpenVpnAuthScriptPath(string relayId)
    {
        return Path.Combine(GetRelayLocalGatewayOpenVpnDirectory(relayId), "auth-verify.ps1");
    }

    public static string GetRelayLocalGatewayOpenVpnAuthCmdPath(string relayId)
    {
        return Path.Combine(GetRelayLocalGatewayOpenVpnDirectory(relayId), "auth-verify.cmd");
    }

    public static string GetRelayLocalGatewayOpenVpnCaPath(string relayId)
    {
        return Path.Combine(GetRelayLocalGatewayOpenVpnDirectory(relayId), "ca.crt");
    }

    public static string GetRelayLocalGatewayOpenVpnServerCertPath(string relayId)
    {
        return Path.Combine(GetRelayLocalGatewayOpenVpnDirectory(relayId), "server.crt");
    }

    public static string GetRelayLocalGatewayOpenVpnServerKeyPath(string relayId)
    {
        return Path.Combine(GetRelayLocalGatewayOpenVpnDirectory(relayId), "server.key");
    }

    public static string GetRelayLocalGatewayOpenVpnTlsCryptKeyPath(string relayId)
    {
        return Path.Combine(GetRelayLocalGatewayOpenVpnDirectory(relayId), "ta.key");
    }

    public static string GetRelayLocalGatewayOpenVpnStdoutLogPath(string relayId)
    {
        return Path.Combine(GetRelayLocalGatewayOpenVpnDirectory(relayId), "openvpn.stdout.log");
    }

    public static string GetRelayLocalGatewayOpenVpnStderrLogPath(string relayId)
    {
        return Path.Combine(GetRelayLocalGatewayOpenVpnDirectory(relayId), "openvpn.stderr.log");
    }

    public static string GetRelayOmniPanelStdoutLogPath(string relayId)
    {
        return Path.Combine(GetRelayLocalGatewayDirectory(relayId), "omnipanel.stdout.log");
    }

    public static string GetRelayOmniPanelStderrLogPath(string relayId)
    {
        return Path.Combine(GetRelayLocalGatewayDirectory(relayId), "omnipanel.stderr.log");
    }

    public static string GetRelayOmniPanelAuthPath(string relayId)
    {
        return Path.Combine(GetRelayLocalGatewayDirectory(relayId), "omnipanel.auth.json");
    }

    public static string GetRelayLocalGatewayAccountingDbPath(string relayId)
    {
        return Path.Combine(GetRelayLocalGatewayDirectory(relayId), "accounting.db");
    }
}
