using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EstherLink.Backend.Pages;

public sealed class DocsModel : PageModel
{
    public string LastUpdated => "2026-03-07";

    public IReadOnlyList<string> Introduction { get; } =
    [
        "OmniRelay is a desktop application for managing secure and reliable internet access in restricted or unstable environments.",
        "You configure it through a guided interface, start relay operations from the UI, and monitor live health indicators from built-in status panels.",
        "This guide focuses on what you see and do in the product so you can operate OmniRelay confidently without learning internal implementation details."
    ];

    public IReadOnlyList<string> SystemRequirements { get; } =
    [
        "Operating system: Windows 10 or Windows 11 (64-bit).",
        "User permissions: local Administrator access is recommended for install/start/stop operations.",
        "Network adapters: at least two active adapters visible in Windows when using split-network scenarios.",
        "Internet expectations: at least one path with outbound access for setup, validation, and license checks.",
        "Remote endpoint access: a reachable gateway server with a user account that can run operational commands.",
        "Time sync: Windows system clock should be accurate for stable status checks and activation flows."
    ];

    public IReadOnlyList<string> InstallationSteps { get; } =
    [
        "Download the latest Windows installer from the official OmniRelay download page.",
        "Run the installer as Administrator.",
        "Follow the setup wizard and complete installation with default options unless your policy requires custom paths.",
        "Launch OmniRelay from the Start Menu after setup completes.",
        "If Windows prompts for permissions, allow access so the app can manage relay operations."
    ];

    public IReadOnlyList<string> FirstLaunchSteps { get; } =
    [
        "On first launch, OmniRelay opens to the onboarding flow and prompts for license activation.",
        "After activation, the main navigation becomes available: Relay Management, Gateway Management, Whitelists, Dashboard, and Settings.",
        "Use the status bar and action logs at the bottom of operation panels to track each command outcome.",
        "Recommended first action: open Relay Management and confirm your network adapters are detected."
    ];

    public IReadOnlyList<string> LicenseActivationSteps { get; } =
    [
        "Open the License page.",
        "Paste your license key into the activation field.",
        "Click Activate License.",
        "Wait for the confirmation message and automatic navigation to operational pages.",
        "If activation fails, verify key accuracy, system time, and internet connectivity, then retry."
    ];

    public IReadOnlyList<ConfigRow> RelayConfigRows { get; } =
    [
        new("Proxy Listen Port", "Main local listening port used by client applications.", "1-65535", "Use an unused port. Keep consistent with your client settings."),
        new("VPS Network (IC1)", "Network adapter used for relay-side connectivity.", "Any detected adapter", "Choose the adapter connected to the relay-side network."),
        new("Outgoing Network (IC2)", "Network adapter used for outbound internet path.", "Any detected adapter", "Choose the adapter that provides your preferred outbound path."),
        new("Apply Relay Config", "Saves current relay field values.", "Button action", "Always apply after editing ports or adapter selections."),
        new("Refresh", "Reloads current status and adapter information.", "Button action", "Use after plugging/unplugging adapters or changing routes.")
    ];

    public IReadOnlyList<string> StartingRelaySteps { get; } =
    [
        "Go to Relay Management.",
        "Set Proxy Listen Port, VPS Network (IC1), and Outgoing Network (IC2).",
        "Click Apply Relay Config.",
        "Click Install/Start Relay.",
        "Use Refresh and verify that Service State is Running and Proxy Running is True.",
        "If you changed configuration after start, click Apply Relay Config again and refresh status."
    ];

    public IReadOnlyList<StatusRow> MonitoringRows { get; } =
    [
        new("Service State", "Shows whether the local relay service is running.", "Running"),
        new("Proxy Running", "Shows whether the listening endpoint is active.", "True"),
        new("Proxy Listen Port", "Displays the active listening port.", "Matches configured value"),
        new("Tunnel Connected", "Shows whether remote connectivity path is currently established.", "True for active sessions"),
        new("Gateway Service State", "Shows remote service readiness from gateway checks.", "Healthy/Active states"),
        new("Health", "Overall health summary from latest check.", "Healthy"),
        new("Last Error", "Latest user-facing error summary.", "Empty or informational only")
    ];

    public IReadOnlyList<TroubleshootRow> TroubleshootingRows { get; } =
    [
        new("Start button completes but status stays stopped", "Open OmniRelay as Administrator and run Install/Start Relay again.", "Confirm Service State changes to Running after Refresh."),
        new("No adapters shown in dropdowns", "Click Refresh, then verify adapters are enabled in Windows Network Settings.", "Adapters appear in IC1/IC2 lists."),
        new("Activation fails with valid key", "Check internet access, system date/time, and key formatting (no extra spaces).", "Activation success message appears."),
        new("Health check shows Unhealthy", "Run action sequence: Test Tunnel -> Bootstrap Check -> Health Check, then review operation log lines.", "Health moves to Healthy or shows clear actionable error."),
        new("Connected earlier but not after outage", "Use Refresh, then rerun Test Tunnel and Start operations to re-establish session.", "Tunnel Connected returns to True."),
        new("Button click seems to do nothing", "Hard refresh browser/UI view and retry; check operation log and bottom status message.", "Action log receives new lines with result."),
        new("Panel unreachable", "Verify correct address/port in UI and allow the port in host and provider firewall rules.", "Panel opens and health checks pass.")
    ];

    public IReadOnlyList<string> UpdateSteps { get; } =
    [
        "Plan a short maintenance window.",
        "From OmniRelay, stop active relay operations.",
        "Download the latest installer package from the official download page.",
        "Run the installer and complete upgrade.",
        "Open OmniRelay and verify your saved configuration values.",
        "Start relay operations and run a full status refresh and health check.",
        "If needed, re-apply relay and gateway config from the UI and validate again."
    ];

    public void OnGet()
    {
    }
}

public sealed record ConfigRow(string Field, string Description, string ValidValues, string OperatorNotes);
public sealed record StatusRow(string Indicator, string Meaning, string ExpectedValue);
public sealed record TroubleshootRow(string Problem, string Action, string SuccessCheck);
