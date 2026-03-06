using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EstherLink.Backend.Pages;

public sealed class DocsModel : PageModel
{
    public string DocumentationUrl { get; private set; } = "/docs";
    public string LastUpdated => "2026-03-06 (based on current implementation defaults)";

    public IReadOnlyList<string> Prerequisites { get; } =
    [
        "Windows machine with OmniRelay UI and Relay service installed.",
        "VPS with Ubuntu 22.04+ and SSH access for the configured tunnel user.",
        "A valid license key to unlock all pages beyond Relay Management.",
        "Two working network adapters on Windows: IC1 (VPS-side) and IC2 (outgoing internet).",
        "Sudo-capable account on VPS for gateway install/start/stop/uninstall operations."
    ];

    public IReadOnlyList<string> QuickStartSteps { get; } =
    [
        "Open OmniRelay UI and activate your license on the License page.",
        "Go to Relay Management and select IC1 + IC2 adapters and proxy port, then click Apply Relay Config.",
        "Click Install/Start Relay and confirm Relay status shows Service Running and Proxy Running=True.",
        "Go to Gateway Management and set Tunnel Host, SSH Port, Tunnel User, auth method, key/password, and tunnel ports.",
        "Set Bootstrap SOCKS ports (local and remote), then click Apply Gateway Config.",
        "Click Test Tunnel and ensure success message appears.",
        "Click Bootstrap Check and wait for SOCKS bootstrap check passed.",
        "Click Install Gateway, then click Health Check.",
        "Go to Whitelists, validate entries, and click Update Whitelist."
    ];

    public IReadOnlyList<FlowStep> DetailedFlow { get; } =
    [
        new(
            "Step 1: License Activation",
            "License page -> enter key -> Activate License.",
            "On success, UI redirects to Relay Management. Pre-license only Relay + License are accessible."
        ),
        new(
            "Step 2: Relay Configuration",
            "Relay Management -> set Proxy Listen Port + IC1/IC2 adapters -> Apply Relay Config.",
            "Status block should eventually show Service Running, Proxy Running=True, and tunnel-related signals once gateway config is applied."
        ),
        new(
            "Step 3: Relay Service Lifecycle",
            "Use Install/Start Relay for first setup, Stop Relay for maintenance, Uninstall Relay for clean reinstall.",
            "Refresh confirms current service and proxy state after each operation."
        ),
        new(
            "Step 4: Gateway Configuration",
            "Gateway Management -> set tunnel endpoint/auth, bootstrap ports, gateway public/panel ports, and DNS mode.",
            "Apply Gateway Config should succeed before tunnel/bootstrap/install actions."
        ),
        new(
            "Step 5: Tunnel and Bootstrap Validation",
            "Click Test Tunnel, then Bootstrap Check.",
            "Operation log should show SOCKS bootstrap check passed before install."
        ),
        new(
            "Step 6: Gateway Install and Runtime Checks",
            "Click Install Gateway, then Health Check.",
            "Gateway state should indicate sshd/x-ui active and health should become healthy once listeners and DNS path are ready."
        ),
        new(
            "Step 7: DNS Through Tunnel",
            "Use Apply DNS Config / Check DNS Path / Repair DNS as needed.",
            "DNS summary should converge to DNS path=true, config=true, rules=true, DoH/UDP readiness according to selected mode."
        ),
        new(
            "Step 8: Whitelist and Ongoing Operations",
            "Whitelist page -> Validate -> Update Whitelist. Use Logs/Dashboard/Settings for daily operations.",
            "Status bar, logs, and page-level feedback provide operation outcomes and errors."
        )
    ];

    public IReadOnlyList<ConfigRow> RelayConfigRows { get; } =
    [
        new("Proxy Listen Port", "19080", "1-65535", "Local Windows CONNECT listener port."),
        new("VPS Network (IC1)", "Auto-picked adapter", "Any detected IPv4 adapter", "Used for whitelist-matched destinations."),
        new("Outgoing Network (IC2)", "Auto-picked adapter", "Any detected IPv4 adapter", "Default egress adapter for non-whitelisted destinations.")
    ];

    public IReadOnlyList<ConfigRow> GatewayConfigRows { get; } =
    [
        new("Tunnel Host", "vps.example.com", "Hostname/IP", "VPS SSH endpoint."),
        new("Tunnel SSH Port", "22", "1-65535", "SSH server port on VPS."),
        new("Tunnel Remote Port", "15000", "1-65535", "Remote forward target consumed by gateway traffic."),
        new("Tunnel User", "estherlink", "Linux username", "Account used by reverse tunnel process."),
        new("Authentication Method", "host_key", "host_key | password", "Controls required secret fields."),
        new("Host Key File", "(empty)", "Existing file path", "Required when auth method is host_key."),
        new("Key Passphrase", "(empty)", "Any string", "Optional/required based on key protection."),
        new("Tunnel Password", "(empty)", "Any string", "Required when auth method is password."),
        new("Bootstrap SOCKS Local Port", "19081", "1-65535", "Windows local SOCKS listener port."),
        new("Bootstrap SOCKS Remote Port", "16080", "1-65535", "VPS loopback SOCKS endpoint via reverse tunnel."),
        new("Gateway Public Port", "443", "1-65535", "Client ingress port on VPS."),
        new("Gateway Panel Port", "2054", "1-65535", "3x-ui panel HTTP port."),
        new("Gateway Backend Port", "15000", "Derived", "Mirrors Tunnel Remote Port automatically.")
    ];

    public IReadOnlyList<ConfigRow> DnsRows { get; } =
    [
        new("DNS Mode", "hybrid", "hybrid | doh | udp", "Hybrid uses DoH + UDP readiness checks."),
        new("DoH Endpoints (CSV)", "https://1.1.1.1/dns-query,https://8.8.8.8/dns-query", "Comma-separated URLs", "Primary DNS-over-HTTPS targets."),
        new("Block Non-DNS UDP", "true", "true | false", "When true, allow UDP/53 only and block other UDP.")
    ];

    public IReadOnlyList<ConfigRow> WhitelistRows { get; } =
    [
        new("Entry Format", "CIDR/IP per line", "IPv4 CIDR or single IP", "Comments with # are allowed."),
        new("Validation", "Manual button", "Validate before update", "Shows per-line parse errors."),
        new("Routing Impact", "Immediate after update", "Whitelist destinations only", "Whitelist entries use IC1 adapter.")
    ];

    public IReadOnlyList<ConfigRow> SettingsRows { get; } =
    [
        new("Dark Theme", "true", "true | false", "UI theme preference."),
        new("Refresh Interval (sec)", "5", "Positive integer", "Background status polling interval."),
        new("Compact Mode", "false", "true | false", "Condensed visual layout preference.")
    ];

    public IReadOnlyList<ActionRow> ActionRows { get; } =
    [
        new("License", "Activate License", "Sends key to service verification flow.", "License state becomes Active and route redirects to Relay.", "Service compatibility/license verification error."),
        new("Relay", "Apply Relay Config", "Writes relay-related config to service.", "Feedback shows Configuration updated.", "Invalid port/adapter selection."),
        new("Relay", "Install/Start Relay", "Installs or starts Windows service and applies config/whitelist/proxy.", "Service State=Running, Proxy Running=True.", "Service install permission or IPC readiness error."),
        new("Relay", "Stop Relay", "Stops Windows service/proxy runtime.", "Service state transitions away from Running.", "Service control denied/not installed."),
        new("Relay", "Uninstall Relay", "Stops and removes Windows service.", "Feedback shows uninstall requested.", "Service removal failure."),
        new("Gateway", "Apply Gateway Config", "Saves tunnel/bootstrap/gateway fields into service config.", "Feedback shows configuration updated.", "Missing/invalid auth or port fields."),
        new("Gateway", "Test Tunnel", "Runs tunnel connectivity/auth check.", "Tunnel connection test succeeded.", "Timeout/auth/connectivity failure."),
        new("Gateway", "Bootstrap Check", "Checks VPS loopback SOCKS endpoint and egress probe through tunnel.", "Gateway bootstrap check passed.", "SOCKS endpoint not reachable or egress probe timeout."),
        new("Gateway", "Install Gateway", "Uploads/runs gateway control script online via bootstrap SOCKS.", "Install completed + panel credentials/URL in operation log.", "Script phase failure shown in operation log."),
        new("Gateway", "Start/Stop/Uninstall", "Controls gateway services on VPS.", "Gateway state updates after refresh/health.", "Sudo/auth/service unit errors."),
        new("Gateway", "Health Check", "Runs gateway health command including DNS readiness.", "Gateway health: Healthy.", "Unhealthy summary with dnsLastError or listener mismatch."),
        new("Gateway", "Apply/Check/Repair DNS", "Applies or verifies DNS-through-tunnel profile.", "DNS summary converges to healthy booleans.", "DoH unreachable/UDP53 not ready/config missing."),
        new("Gateway", "Clear Session Sudo", "Clears cached sudo secret in current UI session.", "Sudo Password Cache=False.", "No-op if no cached value."),
        new("Whitelist", "Validate", "Parses whitelist lines before update.", "Validation summary indicates valid set.", "Line-specific parse errors."),
        new("Whitelist", "Update Whitelist", "Pushes whitelist entries to service.", "Feedback shows updated entry count.", "IPC/service unavailable."),
        new("Dashboard", "Refresh/Verify License/Start Proxy/Stop Proxy", "Quick control and status operations.", "Status cards refresh and action feedback updates.", "Service or license backend errors.")
    ];

    public IReadOnlyList<string> ValidationChecklist { get; } =
    [
        "Relay page: Service State=Running.",
        "Relay page: Proxy Running=True and Proxy Listen Port matches configured value.",
        "Relay page: Tunnel Connected=True after gateway config/tunnel readiness.",
        "Relay page: Bootstrap SOCKS Listening=True and Bootstrap SOCKS Forward=True.",
        "Gateway page: Bootstrap Check=Passed.",
        "Gateway page: Gateway Service State indicates x-ui and sshd active.",
        "Gateway page: Health shows Healthy with current UTC timestamp.",
        "Gateway page: DNS Summary reports path/config/rules healthy for selected mode.",
        "Whitelist page: validation passes and update confirms expected line count."
    ];

    public IReadOnlyList<TroubleshootRow> TroubleshootingRows { get; } =
    [
        new("Bootstrap check times out", "SOCKS remote forward exists but no egress through selected IC2 path.", "Verify IC2 adapter internet reachability, then rerun Bootstrap Check."),
        new("Tunnel reconnect loop with remote port forwarding failed", "Stale remote forward/session on VPS.", "Stop stale SSH sessions for tunnel user on VPS and let relay auto-reconnect."),
        new("Tunnel shows connected but bootstrap forward false", "Tunnel established without expected remote SOCKS forward.", "Re-apply gateway config and retest tunnel/bootstrap; confirm configured remote socks port."),
        new("Install gateway fails early", "Sudo/auth/script prerequisites missing.", "Re-enter session sudo, run Test Tunnel then Bootstrap Check, retry install."),
        new("Panel not reachable", "VPS firewall/provider rules block panel port.", "Open panel port in VPS/provider firewall and verify listener with ss/curl."),
        new("Health unhealthy with DNS errors", "DNS profile missing or DoH/UDP path not available.", "Use Check DNS Path then Repair DNS; switch mode temporarily if network blocks DoH."),
        new("License appears inactive unexpectedly", "Service restart or status refresh race.", "Refresh status and verify license again after service compatibility check."),
        new("UI reports SSH timeout after network outage", "Tunnel session did not recover to a healthy forward state.", "Validate current tunnel endpoint/auth, refresh relay status, and rerun Test Tunnel.")
    ];

    public IReadOnlyList<ScenarioRow> Scenarios { get; } =
    [
        new(
            "First-Time Deployment",
            "New operator onboarding a fresh Windows + VPS pair.",
            "Activate license -> configure/start relay -> configure/test/bootstrap gateway -> install -> health -> whitelist update.",
            "Stable baseline deployment with observable status and logs."
        ),
        new(
            "Reinstall After Outage",
            "Service corruption, stale tunnel sessions, or failed install history.",
            "Stop/uninstall relay or gateway as needed -> re-apply config -> rerun bootstrap check -> reinstall -> health check.",
            "Clean state with restored tunnel forwards and healthy runtime."
        ),
        new(
            "VPS Without Direct Internet",
            "VPS can only install through Windows IC2 egress path.",
            "Ensure Bootstrap SOCKS listening/forward true -> run Bootstrap Check -> Install Gateway online path.",
            "Gateway installs/updates through SOCKS-over-reverse-tunnel flow."
        ),
        new(
            "Browser DNS Failure, Apps Still Work",
            "Client apps connect but browser shows DNS_PROBE errors.",
            "Gateway Management -> Check DNS Path -> Repair DNS (hybrid) -> verify DoH endpoints and UDP53 readiness.",
            "Browser and app DNS both resolve through tunnel policy."
        ),
        new(
            "Controlled Gateway Maintenance Window",
            "Need planned stop/start without changing relay baseline.",
            "Use Gateway Stop/Start and Health Check while leaving relay service running.",
            "Minimal disruption with quick recovery verification."
        )
    ];

    public void OnGet() { }
}

public sealed record ConfigRow(string Field, string DefaultValue, string ValidValues, string Notes);
public sealed record ActionRow(string Area, string Action, string WhatItDoes, string SuccessSignal, string TypicalFailure);
public sealed record TroubleshootRow(string Symptom, string LikelyCause, string Action);
public sealed record ScenarioRow(string Name, string WhenToUse, string Workflow, string ExpectedOutcome);
public sealed record FlowStep(string Title, string Actions, string Validation);
